using Dapper;
using DSI.WorkForce.Core.Licensing;

namespace DSI.WorkForce.Data.Repositories;

public sealed class LicenseRepository(IDbConnectionFactory db)
{
    public async Task<string> GetInstallationIdAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT SettingValue FROM dsi.AppSetting WHERE SettingKey = N'InstallationId'", cancellationToken: ct)) ?? "";
    }

    public async Task<string?> GetCurrentKeyAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT TOP (1) LicenseKey FROM dsi.License WHERE IsCurrent = 1 ORDER BY ActivatedAt DESC", cancellationToken: ct));
    }

    public async Task SaveAsync(string key, LicenseInfo license, string activatedBy, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            SET XACT_ABORT ON;
            BEGIN TRAN;
            UPDATE dsi.License SET IsCurrent = 0 WHERE IsCurrent = 1;
            INSERT dsi.License (LicenseId, LicenseKey, LicensedTo, LicenseType, ExpiresAt, ActivatedBy)
            VALUES (@LicenseId, @key, @LicensedTo, @Type, @ExpiresUtc, @activatedBy);
            COMMIT;
            """, new { license.LicenseId, key, license.LicensedTo, Type = license.Type.ToString(), license.ExpiresUtc, activatedBy }, cancellationToken: ct));
    }

    /// <summary>Highest time this installation has ever seen (protection against turning the clock back).</summary>
    public async Task<DateTime> TouchLastSeenAsync(DateTime utcNow, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var stored = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT SettingValue FROM dsi.AppSetting WHERE SettingKey = N'LastSeenUtc'", cancellationToken: ct));
        var last = DateTime.TryParse(stored, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : DateTime.MinValue;
        if (utcNow > last)
            await conn.ExecuteAsync(new CommandDefinition("""
                MERGE dsi.AppSetting AS t USING (SELECT N'LastSeenUtc' AS SettingKey) AS s ON t.SettingKey = s.SettingKey
                WHEN MATCHED THEN UPDATE SET SettingValue = @v, UpdatedAt = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN INSERT (SettingKey, SettingValue) VALUES (N'LastSeenUtc', @v);
                """, new { v = utcNow.ToString("O") }, cancellationToken: ct));
        return last;
    }
}
