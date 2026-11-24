using DSI.WorkForce.Core.Contracts;
using DSI.WorkForce.Data.Repositories;
using Microsoft.Extensions.Logging;

namespace DSI.WorkForce.Data;

/// <summary>Cached branding settings for every page render (re-read every 30 s and immediately after saving).</summary>
public sealed class BrandingService(SettingsRepository settings, ILogger<BrandingService> log)
{
    private BrandingSettings _current = new();
    private DateTime _loadedAt = DateTime.MinValue;

    public async Task<BrandingSettings> GetAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _loadedAt < TimeSpan.FromSeconds(30)) return _current;
        try { _current = await settings.GetBrandingAsync(ct); }
        catch (Exception ex) { log.LogDebug(ex, "Branding settings not readable - using defaults"); }
        _loadedAt = DateTime.UtcNow;
        return _current;
    }

    public async Task SaveAsync(BrandingSettings value, CancellationToken ct = default)
    {
        await settings.SaveBrandingAsync(value, ct);
        _current = value;
        _loadedAt = DateTime.UtcNow;
    }
}
