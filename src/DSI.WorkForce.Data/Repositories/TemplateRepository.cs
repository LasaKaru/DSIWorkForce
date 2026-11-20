using Dapper;
using DSI.WorkForce.Core.Model;
using DSI.WorkForce.Core.Reporting;
using DSI.WorkForce.Core.Templates;

namespace DSI.WorkForce.Data.Repositories;

public sealed class TemplateRepository(IDbConnectionFactory db)
{
    /// <summary>Inserts the template version (if new) and registers / updates every field in dsi.FieldDefinition.</summary>
    public async Task<bool> UpsertAsync(FormTemplateDefinition t, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();

        var json = TemplateSerializer.Serialize(t);
        var inserted = await conn.ExecuteAsync(new CommandDefinition("""
            IF NOT EXISTS (SELECT 1 FROM dsi.FormTemplate WHERE TemplateCode = @Code AND Version = @Version)
                INSERT dsi.FormTemplate (TemplateCode, Version, Name, DefinitionJson) VALUES (@Code, @Version, @Name, @Json);
            """, new { Code = t.TemplateCode, t.Version, t.Name, Json = json }, tx, cancellationToken: ct)) > 0;

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
        return inserted;
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
