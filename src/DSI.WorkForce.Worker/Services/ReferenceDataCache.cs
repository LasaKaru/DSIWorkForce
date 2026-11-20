using DSI.WorkForce.Core.Matching;
using DSI.WorkForce.Core.Reporting;
using DSI.WorkForce.Core.Templates;
using DSI.WorkForce.Data.Repositories;
using Microsoft.Extensions.Options;

namespace DSI.WorkForce.Worker.Services;

/// <summary>Keeps templates, the field map and master data in memory; refreshed periodically (no DB hit per page).</summary>
public sealed class ReferenceDataCache(TemplateRepository templates, MasterDataRepository masters, IOptions<WorkerOptions> options, ILogger<ReferenceDataCache> log)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTime _loadedAt = DateTime.MinValue;
    private IReadOnlyDictionary<string, FormTemplateDefinition> _templates = new Dictionary<string, FormTemplateDefinition>();
    private IReadOnlyDictionary<string, FieldInfo> _fields = new Dictionary<string, FieldInfo>();
    private MasterDataSnapshot _master = MasterDataSnapshot.Empty;

    public async Task<(FormTemplateDefinition Template, IReadOnlyDictionary<string, FieldInfo> Fields, MasterDataSnapshot Master)> GetAsync(string templateCode, CancellationToken ct)
    {
        if (DateTime.UtcNow - _loadedAt > TimeSpan.FromMinutes(options.Value.MasterDataRefreshMinutes) || !_templates.ContainsKey(templateCode))
            await RefreshAsync(ct);
        if (!_templates.TryGetValue(templateCode, out var t))
            throw new InvalidOperationException($"No active template '{templateCode}' in dsi.FormTemplate. Run DSI.WorkForce.DbMigrator to import templates.");
        return (t, _fields, _master);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _loadedAt < TimeSpan.FromSeconds(10)) return;
            _templates = (await templates.GetActiveAsync(ct)).ToDictionary(t => t.TemplateCode, StringComparer.OrdinalIgnoreCase);
            _fields = await templates.GetFieldMapAsync(ct);
            _master = await masters.LoadSnapshotAsync(ct);
            _loadedAt = DateTime.UtcNow;
            log.LogInformation("Reference data loaded: {Templates} templates, {Employees} employees, {Machines} machines, {Items} items",
                _templates.Count, _master.Employees.Count, _master.Machines.Count, _master.Items.Count);
        }
        finally { _lock.Release(); }
    }
}
