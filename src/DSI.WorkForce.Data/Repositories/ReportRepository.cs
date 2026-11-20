using Dapper;
using DSI.WorkForce.Core.Contracts;
using DSI.WorkForce.Core.Reporting;

namespace DSI.WorkForce.Data.Repositories;

/// <summary>Reads/writes report metadata (rpt.ReportDefinition / rpt.ReportColumn), regenerates the views and runs reports.</summary>
public sealed class ReportRepository(IDbConnectionFactory db, TemplateRepository templates)
{
    public async Task<IReadOnlyList<ReportDefinition>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var defs = (await conn.QueryAsync<DefRow>(new CommandDefinition(
            "SELECT ReportId, Code, Title, Subtitle, TemplateCodes, OnlyApproved, RowField FROM rpt.ReportDefinition ORDER BY Title", cancellationToken: ct))).ToList();
        var cols = (await conn.QueryAsync<ColRow>(new CommandDefinition(
            "SELECT ReportId, ColumnOrder, ColumnKey, Header, SourceType, Source, RowIndex, Cell, Aggregate, Expression, Format, IsVisible FROM rpt.ReportColumn",
            cancellationToken: ct))).ToLookup(c => c.ReportId);
        return defs.Select(d => d.ToModel(cols[d.ReportId])).ToList();
    }

    public async Task<ReportDefinition?> GetAsync(string code, CancellationToken ct = default) =>
        (await GetAllAsync(ct)).FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>Validates, saves the definition (replacing its columns) and rebuilds the SQL view + function.</summary>
    public async Task<ReportSql> SaveAndBuildAsync(ReportDefinition report, CancellationToken ct = default)
    {
        var fields = await templates.GetFieldMapAsync(ct);
        var sql = ReportViewSqlBuilder.Build(report, fields);   // throws on invalid metadata before anything is written

        await using var conn = await db.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        var reportId = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            MERGE rpt.ReportDefinition AS t
            USING (SELECT @Code AS Code) AS s ON t.Code = s.Code
            WHEN MATCHED THEN UPDATE SET Title = @Title, Subtitle = @Subtitle, TemplateCodes = @TemplateCodes, OnlyApproved = @OnlyApproved, RowField = @RowField, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (Code, Title, Subtitle, TemplateCodes, OnlyApproved, RowField) VALUES (@Code, @Title, @Subtitle, @TemplateCodes, @OnlyApproved, @RowField)
            OUTPUT inserted.ReportId;
            """, new
        {
            report.Code, report.Title, report.Subtitle, report.OnlyApproved, RowField = string.IsNullOrWhiteSpace(report.RowField) ? null : report.RowField,
            TemplateCodes = report.TemplateCodes.Count == 0 ? null : string.Join(",", report.TemplateCodes),
        }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition("DELETE rpt.ReportColumn WHERE ReportId = @reportId", new { reportId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT rpt.ReportColumn (ReportId, ColumnOrder, ColumnKey, Header, SourceType, Source, RowIndex, Cell, Aggregate, Expression, Format, IsVisible)
            VALUES (@reportId, @Order, @Key, @Header, @SourceType, @Source, @RowIndex, @Cell, @Aggregate, @Expression, @Format, @Visible);
            """, report.Columns.Select(c => new
        {
            reportId, c.Order, c.Key, c.Header, SourceType = c.SourceType.ToString(), c.Source, c.RowIndex, c.Cell, c.Aggregate, c.Expression, c.Format, c.Visible,
        }), tx, cancellationToken: ct));

        // CREATE VIEW / FUNCTION must each be the only statement in their batch.
        await conn.ExecuteAsync(new CommandDefinition(sql.ViewSql, transaction: tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(sql.FunctionSql, transaction: tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE rpt.ReportDefinition SET LastBuiltAt = SYSUTCDATETIME(), LastBuiltSql = @sql WHERE ReportId = @reportId",
            new { reportId, sql = sql.ViewSql + "\nGO\n" + sql.FunctionSql }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return sql;
    }

    /// <summary>Rebuilds every report (e.g. after importing a new template version).</summary>
    public async Task RebuildAllAsync(CancellationToken ct = default)
    {
        foreach (var r in await GetAllAsync(ct)) await SaveAndBuildAsync(r, ct);
    }

    public async Task<ReportResultDto> QueryAsync(ReportQuery q, int maxRows = 50_000, CancellationToken ct = default)
    {
        var report = await GetAsync(q.ReportCode, ct) ?? throw new KeyNotFoundException($"Report {q.ReportCode} not found.");
        var fn = ReportViewSqlBuilder.Q(report.FunctionName);
        var sheetId = ReportViewSqlBuilder.Q(ReportViewSqlBuilder.SheetIdColumn);
        var rowOrder = report.RowField is null ? "" : ", r." + ReportViewSqlBuilder.Q(ReportViewSqlBuilder.RowNoColumn);

        await using var conn = await db.OpenAsync(ct);
        await using var reader = await conn.ExecuteReaderAsync(new CommandDefinition($"""
            SELECT TOP (@maxRows) r.*
            FROM rpt.{fn}(@from, @to) r
            JOIN dsi.Worksheet w ON w.WorksheetId = r.{sheetId}
            WHERE (@shift IS NULL OR w.Shift = @shift)
              AND (@resourceNo IS NULL OR w.ResourceNo = @resourceNo)
              AND (@epfNo IS NULL OR w.EpfNo = @epfNo)
            ORDER BY w.WorkDate, w.Shift, w.ResourceNo, w.EpfNo, w.WorksheetId{rowOrder};
            """, new
        {
            maxRows, from = q.From.ToDateTime(TimeOnly.MinValue), to = q.To.ToDateTime(TimeOnly.MinValue),
            shift = Blank(q.Shift), resourceNo = Blank(q.ResourceNo), epfNo = Blank(q.EpfNo),
        }, commandTimeout: db.CommandTimeout, cancellationToken: ct));

        var formats = report.Columns.Where(c => c.Visible).ToDictionary(c => c.Header, c => c.Format, StringComparer.OrdinalIgnoreCase);
        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(i => new ReportColumnInfoDto(reader.GetName(i), formats.GetValueOrDefault(reader.GetName(i)))).ToList();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                var v = reader.GetValue(i);
                row[i] = v is DBNull ? null : v is TimeSpan ts ? ts.ToString(@"hh\:mm") : v;
            }
            rows.Add(row);
        }
        return new ReportResultDto(report.Code, report.Title, columns, rows);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private sealed class DefRow
    {
        public int ReportId { get; set; }
        public string Code { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Subtitle { get; set; }
        public string? TemplateCodes { get; set; }
        public bool OnlyApproved { get; set; }
        public string? RowField { get; set; }

        public ReportDefinition ToModel(IEnumerable<ColRow> cols) => new()
        {
            Code = Code, Title = Title, Subtitle = Subtitle, OnlyApproved = OnlyApproved, RowField = RowField,
            TemplateCodes = TemplateCodes?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? [],
            Columns = cols.OrderBy(c => c.ColumnOrder).Select(c => new ReportColumnDefinition
            {
                Order = c.ColumnOrder, Key = c.ColumnKey, Header = c.Header, SourceType = Enum.Parse<ReportSourceType>(c.SourceType),
                Source = c.Source, RowIndex = c.RowIndex, Cell = c.Cell, Aggregate = c.Aggregate, Expression = c.Expression, Format = c.Format, Visible = c.IsVisible,
            }).ToList(),
        };
    }

    private sealed class ColRow
    {
        public int ReportId { get; set; }
        public int ColumnOrder { get; set; }
        public string ColumnKey { get; set; } = "";
        public string Header { get; set; } = "";
        public string SourceType { get; set; } = "";
        public string? Source { get; set; }
        public int? RowIndex { get; set; }
        public int? Cell { get; set; }
        public string? Aggregate { get; set; }
        public string? Expression { get; set; }
        public string? Format { get; set; }
        public bool IsVisible { get; set; }
    }
}
