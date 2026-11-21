using DSI.WorkForce.Core.Licensing;
using DSI.WorkForce.Data.Repositories;
using Microsoft.Extensions.Logging;

namespace DSI.WorkForce.Data;

/// <summary>
/// Current license status for the Web app and the Worker. Re-checked every minute (so an expiry takes effect while
/// running) and immediately after activation. Without a valid license the product is locked.
/// </summary>
public sealed class LicenseService(LicenseRepository repo, UserRepository users, ILogger<LicenseService> log)
{
    public const string SupportEmail = "support@helao2.com";
    public const string Vendor = "Helao2 (Pvt) Ltd";

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromDays(1);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private LicenseCheck _current = new(LicenseStatus.Missing, null, "Checking license…");
    private DateTime _checkedAt = DateTime.MinValue;
    private string _installationId = "";

    public string InstallationId => _installationId;

    public async Task<LicenseCheck> GetAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _checkedAt < CacheFor) return _current;
        await _lock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _checkedAt < CacheFor) return _current;
            _current = await CheckCoreAsync(await repo.GetCurrentKeyAsync(ct), ct);
            _checkedAt = DateTime.UtcNow;
            if (!_current.IsValid) log.LogWarning("License: {Status} - {Message}", _current.Status, _current.Message);
            return _current;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "License check failed (database not reachable?)");
            // Keep the last known state during a short database outage; before the first successful check say why.
            if (_checkedAt == DateTime.MinValue)
                _current = new(LicenseStatus.Missing, null, "The license could not be read because the database is not reachable.");
            return _current;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Validates and stores a key entered on the activation page.</summary>
    public async Task<LicenseCheck> ActivateAsync(string key, string activatedBy, CancellationToken ct = default)
    {
        var check = await CheckCoreAsync(key, ct);
        if (!check.IsValid) return check;
        if (check.License!.MaxUsers is { } max && await users.CountActiveAsync(ct) > max)
            return check with { Status = LicenseStatus.Invalid, Message = $"This license allows {max} active users. Deactivate users first." };

        await repo.SaveAsync(new string(key.Where(c => !char.IsWhiteSpace(c)).ToArray()), check.License, activatedBy, ct);
        await _lock.WaitAsync(ct);
        try { _current = check; _checkedAt = DateTime.UtcNow; }
        finally { _lock.Release(); }
        log.LogInformation("License {Id} activated by {User}: {Message}", check.License.LicenseId, activatedBy, check.Message);
        return check;
    }

    private async Task<LicenseCheck> CheckCoreAsync(string? key, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        _installationId = await repo.GetInstallationIdAsync(ct);
        var lastSeen = await repo.TouchLastSeenAsync(now, ct);
        var check = LicenseCodec.Verify(key, LicensePublicKeys.Trusted, _installationId, now);
        if (check.IsValid && now < lastSeen - ClockTolerance)
            return check with { Status = LicenseStatus.ClockTampered, Message = "The system clock is earlier than the last time the product was used. Correct the date/time." };
        return check;
    }
}
