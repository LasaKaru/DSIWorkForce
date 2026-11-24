using System.Text.Json;
using Dapper;
using DSI.WorkForce.Core.Contracts;

namespace DSI.WorkForce.Data.Repositories;

/// <summary>Key/value application settings in dsi.AppSetting (branding, engine status, installation id ...).</summary>
public sealed class SettingsRepository(IDbConnectionFactory db)
{
    private const string BrandingKey = "Ui.Branding";
    private const string EngineKeyPrefix = "Ocr.Engine.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<BrandingSettings> GetBrandingAsync(CancellationToken ct = default)
    {
        var json = await GetAsync(BrandingKey, ct);
        return json is null ? new BrandingSettings() : JsonSerializer.Deserialize<BrandingSettings>(json, Json) ?? new BrandingSettings();
    }

    public Task SaveBrandingAsync(BrandingSettings settings, CancellationToken ct = default) =>
        SetAsync(BrandingKey, JsonSerializer.Serialize(settings, Json), ct);

    /// <summary>Called by each Worker once its recognition engine is loaded.</summary>
    public Task ReportEngineAsync(EngineStatus status, CancellationToken ct = default) =>
        SetAsync(EngineKeyPrefix + status.Worker, JsonSerializer.Serialize(status, Json), ct);

    public async Task<IReadOnlyList<EngineStatus>> GetEnginesAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT SettingValue FROM dsi.AppSetting WHERE SettingKey LIKE @prefix", new { prefix = EngineKeyPrefix + "%" }, cancellationToken: ct));
        return rows.Select(r => JsonSerializer.Deserialize<EngineStatus>(r, Json)).OfType<EngineStatus>().OrderByDescending(e => e.ReadyUtc).ToList();
    }

    private async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT SettingValue FROM dsi.AppSetting WHERE SettingKey = @key", new { key }, cancellationToken: ct));
    }

    private async Task SetAsync(string key, string value, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            MERGE dsi.AppSetting AS t USING (SELECT @key AS SettingKey) AS s ON t.SettingKey = s.SettingKey
            WHEN MATCHED THEN UPDATE SET SettingValue = @value, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (SettingKey, SettingValue) VALUES (@key, @value);
            """, new { key, value }, cancellationToken: ct));
    }
}
