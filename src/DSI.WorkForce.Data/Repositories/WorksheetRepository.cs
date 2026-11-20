using System.Data;
using System.Globalization;
using Dapper;
using DSI.WorkForce.Core.Contracts;
using DSI.WorkForce.Core.Matching;
using DSI.WorkForce.Core.Model;
using DSI.WorkForce.Core.Normalization;
using DSI.WorkForce.Core.Reporting;
using DSI.WorkForce.Core.Templates;
using Microsoft.Data.SqlClient;

namespace DSI.WorkForce.Data.Repositories;

public sealed class ConcurrencyException(string message) : Exception(message);

public sealed class WorksheetRepository(IDbConnectionFactory db, TemplateRepository templates, MasterDataRepository masters)
{
    /// <summary>
    /// Saves one extracted page atomically (header + values via SqlBulkCopy + issues).
    /// Confidently-blank cells are not stored, which keeps dsi.WorksheetValue roughly half the size.
    /// </summary>
    public async Task<long> SaveExtractionAsync(long scanFileId, WorksheetExtraction ws, string? imagePath,
        IReadOnlyDictionary<string, FieldInfo> fieldMap, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        var worksheetId = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT dsi.Worksheet (ScanFileId, PageNo, TemplateCode, TemplateVersion, WorkDate, Shift, ResourceNo, EpfNo, ItemCode, Status, MinConfidence, ImagePath)
            OUTPUT inserted.WorksheetId
            VALUES (@scanFileId, @PageNo, @TemplateCode, @TemplateVersion, @WorkDate, @Shift, @ResourceNo, @EpfNo, @ItemCode, @Status, @MinConfidence, @imagePath);
            """, new
        {
            scanFileId, ws.PageNo, ws.TemplateCode, ws.TemplateVersion,
            WorkDate = ws.WorkDate?.ToDateTime(TimeOnly.MinValue), ws.Shift, ws.ResourceNo, ws.EpfNo, ws.ItemCode,
            Status = ws.Status.ToString(), MinConfidence = Math.Round(ws.MinConfidence, 4), imagePath,
        }, tx, db.CommandTimeout, cancellationToken: ct));

        var table = CreateValueTable();
        foreach (var v in ws.Values)
        {
            if (v.IsEmpty && v.Confidence >= 0.9m) continue;
            if (!fieldMap.TryGetValue(v.FieldKey, out var fi))
                throw new InvalidOperationException($"Field {v.FieldKey} is not registered in dsi.FieldDefinition. Run the DbMigrator to import templates.");
            table.Rows.Add(worksheetId, fi.FieldId, (byte)v.RowIndex, Trim(v.RawText), Trim(v.ValueText), v.ValueNumber,
                v.ValueDate?.ToDateTime(TimeOnly.MinValue), v.ValueBool, Math.Round(v.Confidence, 4), v.Source.ToString(), false);
        }

        using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.CheckConstraints, tx) { DestinationTableName = "dsi.WorksheetValue", BulkCopyTimeout = db.CommandTimeout })
        {
            foreach (DataColumn c in table.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
            await bulk.WriteToServerAsync(table, ct);
        }

        if (ws.Issues.Count > 0)
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT dsi.ValidationIssue (WorksheetId, FieldKey, RowIndex, Severity, Code, Message)
                VALUES (@worksheetId, @FieldKey, @RowIndex, @Severity, @Code, @Message);
                """, ws.Issues.Select(i => new
            {
                worksheetId, i.FieldKey, RowIndex = (byte)i.RowIndex, Severity = i.Severity.ToString(), i.Code,
                Message = i.Message.Length > 500 ? i.Message[..500] : i.Message,
            }), tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return worksheetId;
    }

    public async Task<IReadOnlyList<WorksheetSummaryDto>> SearchAsync(string? status, DateOnly? from, DateOnly? to, string? resourceNo, string? epfNo, int top, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<SummaryRow>(new CommandDefinition("""
            SELECT TOP (@top) w.WorksheetId, w.TemplateCode, w.WorkDate, w.Shift, w.ResourceNo, w.EpfNo, w.ItemCode, w.Status,
                   w.MinConfidence,
                   (SELECT COUNT(*) FROM dsi.ValidationIssue i WHERE i.WorksheetId = w.WorksheetId AND i.IsResolved = 0) AS OpenIssues,
                   sf.FileName, w.PageNo, w.CreatedAt
            FROM dsi.Worksheet w
            JOIN dsi.ScanFile sf ON sf.ScanFileId = w.ScanFileId
            WHERE (@status IS NULL OR w.Status = @status)
              AND (@from IS NULL OR w.WorkDate >= @from)
              AND (@to IS NULL OR w.WorkDate <= @to)
              AND (@resourceNo IS NULL OR w.ResourceNo = @resourceNo)
              AND (@epfNo IS NULL OR w.EpfNo = @epfNo)
            ORDER BY w.CreatedAt;
            """, new { top, status, from = from?.ToDateTime(TimeOnly.MinValue), to = to?.ToDateTime(TimeOnly.MinValue), resourceNo, epfNo },
            commandTimeout: db.CommandTimeout, cancellationToken: ct));
        return rows.Select(r => r.ToDto()).ToList();
    }

    public async Task<(WorksheetDetailDto Detail, string? ImagePath)?> GetDetailAsync(long worksheetId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        using var multi = await conn.QueryMultipleAsync(new CommandDefinition("""
            SELECT w.WorksheetId, w.TemplateCode, w.WorkDate, w.Shift, w.ResourceNo, w.EpfNo, w.ItemCode, w.Status, w.MinConfidence,
                   (SELECT COUNT(*) FROM dsi.ValidationIssue i WHERE i.WorksheetId = w.WorksheetId AND i.IsResolved = 0) AS OpenIssues,
                   sf.FileName, w.PageNo, w.CreatedAt, w.TemplateVersion, w.ImagePath, w.RowVer
            FROM dsi.Worksheet w JOIN dsi.ScanFile sf ON sf.ScanFileId = w.ScanFileId
            WHERE w.WorksheetId = @worksheetId;

            SELECT f.FieldKey, v.RowIndex, v.RawText, v.ValueText, v.Confidence, v.Source, v.IsCorrected
            FROM dsi.WorksheetValue v JOIN dsi.FieldDefinition f ON f.FieldId = v.FieldId
            WHERE v.WorksheetId = @worksheetId;

            SELECT FieldKey, RowIndex, Severity, Code, Message
            FROM dsi.ValidationIssue WHERE WorksheetId = @worksheetId AND IsResolved = 0;
            """, new { worksheetId }, cancellationToken: ct));

        var head = await multi.ReadSingleOrDefaultAsync<DetailRow>();
        if (head is null) return null;
        var stored = (await multi.ReadAsync<ValueRow>()).ToDictionary(v => (v.FieldKey, (int)v.RowIndex));
        var issues = (await multi.ReadAsync<IssueRow>()).Select(i => new ValidationIssueDto(i.FieldKey, i.RowIndex, i.Severity, i.Code, i.Message)).ToList();

        var template = await templates.GetAsync(head.TemplateCode, head.TemplateVersion, ct)
                       ?? throw new InvalidOperationException($"Template {head.TemplateCode} v{head.TemplateVersion} not found.");

        // Show every cell of the template, including the blank ones that were not stored.
        var values = new List<WorksheetValueDto>();
        foreach (var f in template.Fields)
        {
            var rows = f.IsMultiRow ? Enumerable.Range(1, f.Rows) : [0];
            foreach (var r in rows)
            {
                stored.TryGetValue((f.Key, r), out var s);
                values.Add(new WorksheetValueDto(f.Key, r, f.IsMultiRow ? $"{f.Label} #{r}" : f.Label, f.Group, f.Type.ToString(),
                    s?.RawText, s?.ValueText, s?.Confidence ?? 1m, s?.Source ?? "Omr", s?.IsCorrected ?? false,
                    f.Zone.Row(r == 0 ? 1 : r, f.Rows)));
            }
        }
        return (new WorksheetDetailDto(head.ToDto(), values, issues, head.RowVer), head.ImagePath);
    }

    public async Task SaveCorrectionsAsync(long worksheetId, SaveCorrectionsRequest request, string user, CancellationToken ct = default)
    {
        var detail = await GetDetailAsync(worksheetId, ct) ?? throw new KeyNotFoundException($"Worksheet {worksheetId} not found.");
        var summary = detail.Detail.Summary;
        var template = (await templates.GetActiveAsync(ct)).FirstOrDefault(t => t.TemplateCode == summary.TemplateCode)
                       ?? throw new InvalidOperationException("Template not found.");
        var fieldMap = await templates.GetFieldMapAsync(ct);
        var master = await masters.LoadSnapshotAsync(ct);
        var refDate = summary.WorkDate ?? DateOnly.FromDateTime(summary.CreatedAt);
        var existing = detail.Detail.Values.ToDictionary(v => (v.FieldKey, v.RowIndex));

        await using var conn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        // Optimistic concurrency: fail if somebody else saved this sheet since it was loaded.
        var touched = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dsi.Worksheet SET MinConfidence = MinConfidence WHERE WorksheetId = @worksheetId AND (@rv IS NULL OR RowVer = @rv)",
            new { worksheetId, rv = request.RowVersion }, tx, cancellationToken: ct));
        if (touched == 0) throw new ConcurrencyException("This sheet was changed by someone else. Reload and try again.");

        foreach (var c in request.Corrections)
        {
            var field = template.Find(c.FieldKey) ?? throw new InvalidOperationException($"Unknown field {c.FieldKey}.");
            var fi = fieldMap[field.Key];
            var v = ValueNormalizer.Normalize(field, c.RowIndex, c.Value, 1m, ValueSource.Manual, refDate);
            existing.TryGetValue((c.FieldKey, c.RowIndex), out var old);
            if (string.Equals(old?.Value ?? "", v.ValueText ?? "", StringComparison.Ordinal)) continue;

            // Item corrections: accept an item code or description from the master and learn the old spelling.
            if (field.Lookup == "Item" && v.ValueText is not null)
            {
                var item = master.Items.FirstOrDefault(i => string.Equals(i.ItemCode, v.ValueText, StringComparison.OrdinalIgnoreCase)
                                                         || string.Equals(i.Description, v.ValueText, StringComparison.OrdinalIgnoreCase));
                if (item is not null)
                {
                    v.ValueText = item.Description;
                    await conn.ExecuteAsync(new CommandDefinition("UPDATE dsi.Worksheet SET ItemCode = @code WHERE WorksheetId = @worksheetId",
                        new { code = item.ItemCode, worksheetId }, tx, cancellationToken: ct));
                    if (!string.IsNullOrWhiteSpace(old?.RawText))
                        await conn.ExecuteAsync(new CommandDefinition("""
                            IF NOT EXISTS (SELECT 1 FROM dsi.ItemAlias WHERE Alias = @alias)
                                INSERT dsi.ItemAlias (Alias, ItemCode, CreatedBy) VALUES (@alias, @code, @user);
                            """, new { alias = FuzzyMatcher.CanonicalItem(old.RawText!), code = item.ItemCode, user }, tx, cancellationToken: ct));
                }
            }

            await conn.ExecuteAsync(new CommandDefinition("""
                MERGE dsi.WorksheetValue AS t
                USING (SELECT @worksheetId AS WorksheetId, @FieldId AS FieldId, @RowIndex AS RowIndex) AS s
                   ON t.WorksheetId = s.WorksheetId AND t.FieldId = s.FieldId AND t.RowIndex = s.RowIndex
                WHEN MATCHED THEN UPDATE SET ValueText = @ValueText, ValueNumber = @ValueNumber, ValueDate = @ValueDate, ValueBool = @ValueBool,
                                             Confidence = 1, Source = N'Manual', IsCorrected = 1
                WHEN NOT MATCHED THEN INSERT (WorksheetId, FieldId, RowIndex, RawText, ValueText, ValueNumber, ValueDate, ValueBool, Confidence, Source, IsCorrected)
                                      VALUES (@worksheetId, @FieldId, @RowIndex, NULL, @ValueText, @ValueNumber, @ValueDate, @ValueBool, 1, N'Manual', 1);

                INSERT dsi.ValueAudit (WorksheetId, FieldId, RowIndex, OldValue, NewValue, ChangedBy)
                VALUES (@worksheetId, @FieldId, @RowIndex, @OldValue, @ValueText, @user);

                UPDATE dsi.ValidationIssue SET IsResolved = 1, ResolvedBy = @user, ResolvedAt = SYSUTCDATETIME()
                WHERE WorksheetId = @worksheetId AND FieldKey = @FieldKey AND RowIndex = @RowIndex AND IsResolved = 0;
                """, new
            {
                worksheetId, fi.FieldId, RowIndex = (byte)c.RowIndex, v.ValueText, v.ValueNumber,
                ValueDate = v.ValueDate?.ToDateTime(TimeOnly.MinValue), v.ValueBool, OldValue = old?.Value, user, FieldKey = field.Key,
            }, tx, cancellationToken: ct));

            if (field.PromoteTo is { } col && col != "ItemCode")
            {
                object? promoted = col == "WorkDate" ? v.ValueDate?.ToDateTime(TimeOnly.MinValue) : v.ValueText;
                // Column name comes from a fixed whitelist (WorksheetHeaderColumns.Promotable), never from user input.
                if (WorksheetHeaderColumns.Promotable.Contains(col))
                    await conn.ExecuteAsync(new CommandDefinition($"UPDATE dsi.Worksheet SET [{col}] = @promoted WHERE WorksheetId = @worksheetId",
                        new { promoted, worksheetId }, tx, cancellationToken: ct));
            }
        }

        if (request.MarkVerified)
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE dsi.Worksheet SET Status = N'Verified', VerifiedBy = @user, VerifiedAt = SYSUTCDATETIME(), MinConfidence = 1 WHERE WorksheetId = @worksheetId;
                UPDATE dsi.ValidationIssue SET IsResolved = 1, ResolvedBy = @user, ResolvedAt = SYSUTCDATETIME() WHERE WorksheetId = @worksheetId AND IsResolved = 0;
                """, new { worksheetId, user }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
    }

    public async Task RejectAsync(long worksheetId, string user, string reason, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE dsi.Worksheet SET Status = N'Rejected', VerifiedBy = @user, VerifiedAt = SYSUTCDATETIME() WHERE WorksheetId = @worksheetId;
            INSERT dsi.ValidationIssue (WorksheetId, FieldKey, RowIndex, Severity, Code, Message, IsResolved, ResolvedBy, ResolvedAt)
            VALUES (@worksheetId, NULL, 0, N'Info', N'REJECTED', LEFT(@reason, 500), 1, @user, SYSUTCDATETIME());
            """, new { worksheetId, user, reason }, cancellationToken: ct));
    }

    public async Task<DashboardDto> GetDashboardAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleAsync<DashboardDto>(new CommandDefinition("""
            DECLARE @today DATETIME2(3) = CAST(CAST(SYSUTCDATETIME() AS DATE) AS DATETIME2(3));
            SELECT
              (SELECT COUNT(*) FROM dsi.ScanFile WHERE Status = N'Pending')    AS PendingFiles,
              (SELECT COUNT(*) FROM dsi.ScanFile WHERE Status = N'Processing') AS ProcessingFiles,
              (SELECT COUNT(*) FROM dsi.ScanFile WHERE Status = N'Failed')     AS FailedFiles,
              (SELECT COUNT(*) FROM dsi.Worksheet WHERE CreatedAt >= @today)   AS SheetsToday,
              (SELECT COUNT(*) FROM dsi.Worksheet WHERE CreatedAt >= @today AND Status = N'AutoApproved') AS AutoApprovedToday,
              (SELECT COUNT(*) FROM dsi.Worksheet WHERE Status = N'NeedsReview') AS NeedsReviewTotal,
              (SELECT COUNT(*) FROM dsi.Worksheet WHERE VerifiedAt >= @today AND Status = N'Verified') AS VerifiedToday,
              (SELECT MAX(LastSeenUtc) FROM dsi.WorkerHeartbeat) AS LastWorkerHeartbeatUtc;
            """, cancellationToken: ct));
    }

    private static string? Trim(string? s) => s is null ? null : s.Length > 400 ? s[..400] : s;

    private static DataTable CreateValueTable()
    {
        var t = new DataTable();
        t.Columns.Add("WorksheetId", typeof(long));
        t.Columns.Add("FieldId", typeof(int));
        t.Columns.Add("RowIndex", typeof(byte));
        t.Columns.Add("RawText", typeof(string));
        t.Columns.Add("ValueText", typeof(string));
        t.Columns.Add("ValueNumber", typeof(decimal));
        t.Columns.Add("ValueDate", typeof(DateTime));
        t.Columns.Add("ValueBool", typeof(bool));
        t.Columns.Add("Confidence", typeof(decimal));
        t.Columns.Add("Source", typeof(string));
        t.Columns.Add("IsCorrected", typeof(bool));
        foreach (DataColumn c in t.Columns) c.AllowDBNull = true;
        return t;
    }

    // Dapper row types (DATE columns arrive as DateTime).
    private class SummaryRow
    {
        public long WorksheetId { get; set; }
        public string TemplateCode { get; set; } = "";
        public DateTime? WorkDate { get; set; }
        public string? Shift { get; set; }
        public string? ResourceNo { get; set; }
        public string? EpfNo { get; set; }
        public string? ItemCode { get; set; }
        public string Status { get; set; } = "";
        public decimal MinConfidence { get; set; }
        public int OpenIssues { get; set; }
        public string FileName { get; set; } = "";
        public int PageNo { get; set; }
        public DateTime CreatedAt { get; set; }

        public WorksheetSummaryDto ToDto() => new(WorksheetId, TemplateCode,
            WorkDate is null ? null : DateOnly.FromDateTime(WorkDate.Value), Shift, ResourceNo, EpfNo, ItemCode, Status,
            MinConfidence, OpenIssues, FileName, PageNo, DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc));
    }

    private sealed class DetailRow : SummaryRow
    {
        public int TemplateVersion { get; set; }
        public string? ImagePath { get; set; }
        public byte[]? RowVer { get; set; }
    }

    private sealed class ValueRow
    {
        public string FieldKey { get; set; } = "";
        public byte RowIndex { get; set; }
        public string? RawText { get; set; }
        public string? ValueText { get; set; }
        public decimal Confidence { get; set; }
        public string Source { get; set; } = "";
        public bool IsCorrected { get; set; }
    }

    private sealed class IssueRow
    {
        public string? FieldKey { get; set; }
        public byte RowIndex { get; set; }
        public string Severity { get; set; } = "";
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
    }
}
