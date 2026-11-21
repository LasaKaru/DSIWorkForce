using Dapper;
using DSI.WorkForce.Core.Contracts;
using DSI.WorkForce.Core.Security;

namespace DSI.WorkForce.Data.Repositories;

public sealed record UserAuthRecord(int UserId, string UserName, string FullName, string PasswordHash, bool IsActive,
    bool MustChangePassword, Guid SecurityStamp, int FailedLogins, DateTime? LockedUntil);

public enum LoginOutcome { Success, InvalidCredentials, LockedOut, Inactive }

public sealed class UserRepository(IDbConnectionFactory db)
{
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(15);

    public async Task<(LoginOutcome Outcome, UserAuthRecord? User, IReadOnlyList<string> Permissions)> LoginAsync(string userName, string password, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var u = await conn.QuerySingleOrDefaultAsync<UserAuthRecord>(new CommandDefinition("""
            SELECT UserId, UserName, FullName, PasswordHash, IsActive, MustChangePassword, SecurityStamp, FailedLogins, LockedUntil
            FROM dsi.AppUser WHERE UserName = @userName
            """, new { userName = userName.Trim() }, cancellationToken: ct));

        if (u is null)
        {
            PasswordHasher.Verify(password, PasswordHasher.Hash("timing-equaliser"));   // same work as a real check
            return (LoginOutcome.InvalidCredentials, null, []);
        }
        if (u.LockedUntil > DateTime.UtcNow) return (LoginOutcome.LockedOut, u, []);
        if (!PasswordHasher.Verify(password, u.PasswordHash))
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE dsi.AppUser SET FailedLogins = FailedLogins + 1,
                       LockedUntil = CASE WHEN FailedLogins + 1 >= @max THEN DATEADD(MINUTE, @mins, SYSUTCDATETIME()) END
                WHERE UserId = @id
                """, new { id = u.UserId, max = MaxFailedLogins, mins = (int)LockoutTime.TotalMinutes }, cancellationToken: ct));
            return (LoginOutcome.InvalidCredentials, u, []);
        }
        if (!u.IsActive) return (LoginOutcome.Inactive, u, []);

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dsi.AppUser SET FailedLogins = 0, LockedUntil = NULL, LastLoginAt = SYSUTCDATETIME() WHERE UserId = @id",
            new { id = u.UserId }, cancellationToken: ct));
        var perms = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT Permission FROM dsi.AppUserPermission WHERE UserId = @id", new { id = u.UserId }, cancellationToken: ct))).ToList();
        return (LoginOutcome.Success, u, perms);
    }

    /// <summary>Used to sign out sessions when a user is deactivated, its permissions change or its password is reset.</summary>
    public async Task<(bool IsActive, Guid Stamp)?> GetStampAsync(int userId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<(bool, Guid)?>(new CommandDefinition(
            "SELECT IsActive, SecurityStamp FROM dsi.AppUser WHERE UserId = @userId", new { userId }, cancellationToken: ct));
        return row;
    }

    public async Task<IReadOnlyList<UserRow>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        using var multi = await conn.QueryMultipleAsync(new CommandDefinition("""
            SELECT UserId, UserName, FullName, Email, IsActive, MustChangePassword, LastLoginAt, LockedUntil FROM dsi.AppUser ORDER BY UserName;
            SELECT UserId, Permission FROM dsi.AppUserPermission;
            """, cancellationToken: ct));
        var users = (await multi.ReadAsync<UserRow>()).ToList();
        var perms = (await multi.ReadAsync<(int UserId, string Permission)>()).ToLookup(p => p.UserId, p => p.Permission);
        foreach (var u in users) u.Permissions = new HashSet<string>(perms[u.UserId], StringComparer.OrdinalIgnoreCase);
        return users;
    }

    public async Task<int> CountActiveAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM dsi.AppUser WHERE IsActive = 1", cancellationToken: ct));
    }

    /// <summary>Creates (UserId = 0) or updates a user and its permissions. Password only when creating / resetting.</summary>
    public async Task<int> SaveAsync(UserRow user, string? newPassword, CancellationToken ct = default)
    {
        foreach (var p in user.Permissions)
            if (!Permissions.IsKnown(p)) throw new InvalidOperationException($"Unknown permission '{p}'.");

        await using var conn = await db.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();

        int id;
        if (user.UserId == 0)
        {
            if (string.IsNullOrEmpty(newPassword)) throw new InvalidOperationException("A password is required for a new user.");
            id = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
                INSERT dsi.AppUser (UserName, FullName, Email, PasswordHash, IsActive, MustChangePassword)
                OUTPUT inserted.UserId VALUES (@UserName, @FullName, @Email, @hash, @IsActive, @MustChangePassword)
                """, new { UserName = user.UserName.Trim(), user.FullName, user.Email, hash = PasswordHasher.Hash(newPassword), user.IsActive, user.MustChangePassword },
                tx, cancellationToken: ct));
        }
        else
        {
            id = user.UserId;
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE dsi.AppUser SET FullName = @FullName, Email = @Email, IsActive = @IsActive, MustChangePassword = @MustChangePassword,
                       SecurityStamp = NEWID(), UpdatedAt = SYSUTCDATETIME(),
                       PasswordHash = COALESCE(@hash, PasswordHash),
                       FailedLogins = CASE WHEN @hash IS NULL THEN FailedLogins ELSE 0 END,
                       LockedUntil = CASE WHEN @hash IS NULL THEN LockedUntil END
                WHERE UserId = @UserId
                """, new
            {
                user.UserId, user.FullName, user.Email, user.IsActive, user.MustChangePassword,
                hash = string.IsNullOrEmpty(newPassword) ? null : PasswordHasher.Hash(newPassword),
            }, tx, cancellationToken: ct));
        }

        await conn.ExecuteAsync(new CommandDefinition("DELETE dsi.AppUserPermission WHERE UserId = @id", new { id }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("INSERT dsi.AppUserPermission (UserId, Permission) VALUES (@id, @p)",
            user.Permissions.Select(p => new { id, p }), tx, cancellationToken: ct));

        // Never leave the system without an active administrator.
        var admins = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM dsi.AppUser u JOIN dsi.AppUserPermission p ON p.UserId = u.UserId
            WHERE u.IsActive = 1 AND p.Permission IN (N'Admin', N'Users')
            """, transaction: tx, cancellationToken: ct));
        if (admins == 0) throw new InvalidOperationException("At least one active user must keep the Administrator or User management permission.");

        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<bool> ChangeOwnPasswordAsync(int userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var hash = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT PasswordHash FROM dsi.AppUser WHERE UserId = @userId AND IsActive = 1", new { userId }, cancellationToken: ct));
        if (hash is null || !PasswordHasher.Verify(currentPassword, hash)) return false;
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE dsi.AppUser SET PasswordHash = @h, MustChangePassword = 0, SecurityStamp = NEWID(), UpdatedAt = SYSUTCDATETIME()
            WHERE UserId = @userId
            """, new { userId, h = PasswordHasher.Hash(newPassword) }, cancellationToken: ct));
        return true;
    }

    /// <summary>Creates the first administrator when the user table is empty. Returns true if it was created.</summary>
    public async Task<bool> EnsureDefaultAdminAsync(string userName, string password, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        if (await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM dsi.AppUser", cancellationToken: ct)) > 0) return false;
        await SaveAsync(new UserRow
        {
            UserName = userName, FullName = "Administrator", IsActive = true, MustChangePassword = true,
            Permissions = new HashSet<string>([Permissions.Admin], StringComparer.OrdinalIgnoreCase),
        }, password, ct);
        return true;
    }
}
