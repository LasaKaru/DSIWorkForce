using Dapper;
using DSI.WorkForce.Core.Model;
using DSI.WorkForce.Core.Reporting;
using DSI.WorkForce.Core.Templates;

namespace DSI.WorkForce.Data.Repositories;

public enum TemplateImportResult { Unchanged, Inserted, Updated }

public sealed class TemplateRepository(IDbConnectionFactory db)
{
    /// <summary>
    /// Imports a template version. New version: inserted. Same version with a different layout (e.g. zones adjusted
    /// while calibrating): updated. Identical: nothing is written. Fields are registered/updated in dsi.FieldDefinition
    /// only when something changed, so a normal application start takes no write locks.
    /// </summary>
    public async Task<TemplateImportResult> UpsertAsync(FormTemplateDefinition t, CancellationToken ct = default)
    {
        var json = TemplateSerializer.Serialize(t);
        await using var conn = await db.OpenAsync(ct);

        var stored = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT DefinitionJson FROM dsi.FormTemplate WHERE TemplateCode = @Code AND Version = @Version",
            new { Code = t.TemplateCode, t.Version }, cancellationToken: ct));
        if (stored == json) return TemplateImportResult.Unchanged;

        await using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(new CommandDefinition(stored is null
                ? "INSERT dsi.FormTemplate (TemplateCode, Version, Name, DefinitionJson) VALUES (@Code, @Version, @Name, @Json);"
                : "UPDATE dsi.FormTemplate SET Name = @Name, DefinitionJson = @Json WHERE TemplateCode = @Code AND Version = @Version;",
            new { Code = t.TemplateCode, t.Version, t.Name, Json = json }, tx, cancellationToken: ct));

        foreach (var f in t.Fields)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                MERGE dsi.FieldDefinition AS tgt
                USING (SELECT @Key AS FieldKey) AS src ON tgt.FieldKey = src.FieldKey
                WHEN MATCHED THEN UPDATE SET Label = @Label, DataType = @DataType, GroupName = @GroupName, GridRows = @Rows, GridColumns = @Columns, IsActive = 1
                WHEN NOT MATCHED THEN INSERT (FieldKey, Label, DataType, GroupName, GridRows, GridColumns) VALUES (@Key, @Label, @DataType, @GroupName, @Rows, @Columns);
                """, new { f.Key, f.Label, DataType = f.Type.ToString(), GroupName = f.Group, f.Rows, Columns = f.IsGrid ? f.Columns : 1 }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return stored is null ? TemplateImportResult.Inserted : TemplateImportResult.Updated;
    }

    /// <summary>Latest active version of every template code.</summary>
    public async Task<IReadOnlyList<FormTemplateDefinition>> GetActiveAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT DefinitionJson FROM (
                SELECT DefinitionJson, ROW_NUMBER() OVER (PARTITION BY TemplateCode ORDER BY Version DESC) AS rn
                FROM dsi.FormTemplate WHERE IsActive = 1) x
            WHERE rn = 1;
            """, cancellationToken: ct));
        return rows.Select(TemplateSerializer.Deserialize).ToList();
    }

    public async Task<FormTemplateDefinition?> GetAsync(string code, int version, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var json = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT DefinitionJson FROM dsi.FormTemplate WHERE TemplateCode = @code AND Version = @version",
            new { code, version }, cancellationToken: ct));
        return json is null ? null : TemplateSerializer.Deserialize(json);
    }

    public async Task<IReadOnlyDictionary<string, FieldInfo>> GetFieldMapAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(int FieldId, string FieldKey, string DataType, int GridRows, int GridColumns)>(new CommandDefinition(
            "SELECT FieldId, FieldKey, DataType, GridRows, GridColumns FROM dsi.FieldDefinition", cancellationToken: ct));
        return rows.ToDictionary(r => r.FieldKey,
            r => new FieldInfo(r.FieldId, r.FieldKey, Enum.Parse<FieldDataType>(r.DataType), r.GridRows, r.GridColumns), StringComparer.OrdinalIgnoreCase);
    }
}
