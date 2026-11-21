using System.Data;
using System.Globalization;
using Dapper;
using DSI.WorkForce.Core.Contracts;
using DSI.WorkForce.Core.Matching;
using DSI.WorkForce.Core.Model;
using DSI.WorkForce.Core.Normalization;
using DSI.WorkForce.Core.Reporting;
using DSI.WorkForce.Core.Templates;
using DSI.WorkForce.Core.Validation;
using Microsoft.Data.SqlClient;

namespace DSI.WorkForce.Data.Repositories;

public sealed class ConcurrencyException(string message) : Exception(message);

public sealed class WorksheetRepository(IDbConnectionFactory db, TemplateRepository templates, MasterDataRepository masters)
{
    // 11 columns, 10 parameters per row (WorksheetId shared): 150 rows = 1,501 parameters (SQL Server limit 2,100).
    private const int RowsPerInsert = 150;

    /// <summary>
    /// Saves one extracted page atomically (header + values + issues in one transaction).
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

        // Plain parameterized multi-row INSERTs (normal row locks; ~120 values per sheet, so 1 round trip).
        var rows = new List<(int FieldId, ExtractedValue V)>();
        foreach (var v in ws.Values)
        {
            if (v.IsEmpty && v.Confidence >= 0.9m) continue;   // confidently blank cells are not stored
            if (!fieldMap.TryGetValue(v.FieldKey, out var fi))
                throw new InvalidOperationException($"Field {v.FieldKey} is not registered in dsi.FieldDefinition. Run the DbMigrator to import templates.");
            rows.Add((fi.FieldId, v));
        }
        foreach (var chunk in rows.Chunk(RowsPerInsert))
        {
            var p = new DynamicParameters();
            p.Add("w", worksheetId, DbType.Int64);
            for (var i = 0; i < chunk.Length; i++)
            {
                var (fieldId, v) = chunk[i];
                p.Add($"f{i}", fieldId, DbType.Int32);
                p.Add($"r{i}", (byte)v.RowIndex, DbType.Byte);
                p.Add($"raw{i}", Trim(v.RawText), DbType.String, size: 400);
                p.Add($"t{i}", Trim(v.ValueText), DbType.String, size: 400);
                p.Add($"n{i}", v.ValueNumber, DbType.Decimal, precision: 18, scale: 4);
                p.Add($"d{i}", v.ValueDate?.ToDateTime(TimeOnly.MinValue), DbType.Date);
                p.Add($"b{i}", v.ValueBool, DbType.Boolean);
                p.Add($"c{i}", Math.Round(v.Confidence, 4), DbType.Decimal, precision: 5, scale: 4);
                p.Add($"s{i}", v.Source.ToString(), DbType.String, size: 10);
            }
            await conn.ExecuteAsync(new CommandDefinition(BuildValuesInsertSql(chunk.Length), p, tx, db.CommandTimeout, cancellationToken: ct));
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
            foreach (var r in f.RowIndexes())
            {
                stored.TryGetValue((f.Key, r), out var s);
                var (gridRow, gridCol) = f.IsGrid ? f.CellOf(r) : (0, 0);
                var label = f.IsGrid ? $"{f.Label} - hour {gridRow}, cell {gridCol}" : f.IsMultiRow ? $"{f.Label} #{r}" : f.Label;
                values.Add(new WorksheetValueDto(f.Key, r, label, f.Group, f.Type.ToString(),
                    s?.RawText, s?.ValueText, s?.Confidence ?? 1m, s?.Source ?? "Omr", s?.IsCorrected ?? false,
                    f.ZoneOf(r), gridRow, gridCol, f.Extractor == ExtractorKind.Computed));
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
        var corrected = new Dictionary<(string, int), ExtractedValue>();

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
            if (field.Extractor == ExtractorKind.Computed) continue;   // sums are recalculated below, never typed
            if (string.Equals(old?.Value ?? "", v.ValueText ?? "", StringComparison.Ordinal)) continue;
            corrected[(field.Key, c.RowIndex)] = v;

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

        await RecalculateComputedAsync(conn, tx, worksheetId, template, fieldMap, existing, corrected, ct);

        if (request.MarkVerified)
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE dsi.Worksheet SET Status = N'Verified', VerifiedBy = @user, VerifiedAt = SYSUTCDATETIME(), MinConfidence = 1 WHERE WorksheetId = @worksheetId;
                UPDATE dsi.ValidationIssue SET IsResolved = 1, ResolvedBy = @user, ResolvedAt = SYSUTCDATETIME() WHERE WorksheetId = @worksheetId AND IsResolved = 0;
                """, new { worksheetId, user }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
    }

    /// <summary>Re-sums computed fields (hourly units) from the current cell values after a reviewer edited cells.</summary>
    private static async Task RecalculateComputedAsync(SqlConnection conn, SqlTransaction tx, long worksheetId, FormTemplateDefinition template,
        IReadOnlyDictionary<string, FieldInfo> fieldMap, IReadOnlyDictionary<(string, int), WorksheetValueDto> existing,
        IReadOnlyDictionary<(string, int), ExtractedValue> corrected, CancellationToken ct)
    {
        var computedFields = template.Fields.Where(f => f.Extractor == ExtractorKind.Computed && f.SumOf is not null).ToList();
        if (computedFields.Count == 0 || corrected.Count == 0) return;

        var ws = new WorksheetExtraction { TemplateCode = template.TemplateCode, PageNo = 0 };
        foreach (var src in computedFields.Select(f => template.Find(f.SumOf!)).OfType<FieldDefinition>().Distinct())
            foreach (var r in src.RowIndexes())
            {
                if (corrected.TryGetValue((src.Key, r), out var cv)) { ws.Values.Add(cv); continue; }
                existing.TryGetValue((src.Key, r), out var ev);
                ws.Values.Add(new ExtractedValue
                {
                    FieldKey = src.Key, RowIndex = r, ValueText = ev?.Value, Confidence = ev?.Confidence ?? 1m,
                    ValueNumber = decimal.TryParse(ev?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : null,
                });
            }
        ComputedFields.Apply(ws, template);

        foreach (var f in computedFields)
            foreach (var v in ws.GetRows(f.Key))
                await conn.ExecuteAsync(new CommandDefinition("""
                    DELETE dsi.WorksheetValue WHERE WorksheetId = @worksheetId AND FieldId = @FieldId AND RowIndex = @RowIndex;
                    IF @ValueNumber IS NOT NULL OR @Confidence < 0.9
                        INSERT dsi.WorksheetValue (WorksheetId, FieldId, RowIndex, RawText, ValueText, ValueNumber, Confidence, Source, IsCorrected)
                        VALUES (@worksheetId, @FieldId, @RowIndex, @RawText, @ValueText, @ValueNumber, @Confidence, N'Computed', 1);
                    """, new
                {
                    worksheetId, fieldMap[f.Key].FieldId, RowIndex = (byte)v.RowIndex, v.RawText, v.ValueText, v.ValueNumber, v.Confidence,
                }, tx, cancellationToken: ct));
    }

    /// <summary>
    /// Throws away this page's readings and queues its file again (the Worker re-reads only missing pages).
    /// Use after switching on OCR, changing the template or calibrating. Corrections audit is kept.
    /// </summary>
    public async Task ReprocessAsync(long worksheetId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            SET XACT_ABORT ON;
            BEGIN TRAN;
            DECLARE @file BIGINT = (SELECT ScanFileId FROM dsi.Worksheet WHERE WorksheetId = @worksheetId);
            DELETE dsi.Worksheet WHERE WorksheetId = @worksheetId;   -- values and issues are deleted by cascade
            UPDATE dsi.ScanFile
               SET Status = N'Pending', Attempts = 0, NextAttemptAt = SYSUTCDATETIME(), LockedBy = NULL, LockedUntil = NULL, LastError = NULL
             WHERE ScanFileId = @file;
            COMMIT;
            """, new { worksheetId }, cancellationToken: ct));
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

    /// <summary>Multi-row INSERT for <paramref name="rows"/> values; parameters @w, @f0.., @r0.., ... (see SaveExtractionAsync).</summary>
    public static string BuildValuesInsertSql(int rows) =>
        "INSERT dsi.WorksheetValue (WorksheetId, FieldId, RowIndex, RawText, ValueText, ValueNumber, ValueDate, ValueBool, Confidence, Source, IsCorrected) VALUES " +
        string.Join(",", Enumerable.Range(0, rows).Select(i => $"(@w,@f{i},@r{i},@raw{i},@t{i},@n{i},@d{i},@b{i},@c{i},@s{i},0)"));

    public sealed record DailyCount(DateTime Day, int Sheets, int AutoApproved, int Verified, int NeedsReview);

    /// <summary>Sheets scanned per day (dashboard chart).</summary>
    public async Task<IReadOnlyList<DailyCount>> GetDailyCountsAsync(int days, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync<DailyCount>(new CommandDefinition("""
            SELECT CAST(CreatedAt AS DATE) AS Day, COUNT(*) AS Sheets,
                   SUM(CASE WHEN Status = N'AutoApproved' THEN 1 ELSE 0 END) AS AutoApproved,
                   SUM(CASE WHEN Status = N'Verified' THEN 1 ELSE 0 END) AS Verified,
                   SUM(CASE WHEN Status = N'NeedsReview' THEN 1 ELSE 0 END) AS NeedsReview
            FROM dsi.Worksheet
            WHERE CreatedAt >= DATEADD(DAY, -@days, CAST(SYSUTCDATETIME() AS DATE))
            GROUP BY CAST(CreatedAt AS DATE)
            """, new { days }, cancellationToken: ct))).ToDictionary(r => r.Day.Date);
        var today = DateTime.UtcNow.Date;
        return Enumerable.Range(0, days).Select(i => today.AddDays(i - days + 1))
            .Select(d => rows.TryGetValue(d, out var r) ? r : new DailyCount(d, 0, 0, 0, 0)).ToList();
    }

    public async Task<IReadOnlyList<WorksheetSummaryDto>> GetRecentAsync(int top, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<SummaryRow>(new CommandDefinition("""
            SELECT TOP (@top) w.WorksheetId, w.TemplateCode, w.WorkDate, w.Shift, w.ResourceNo, w.EpfNo, w.ItemCode, w.Status, w.MinConfidence,
                   (SELECT COUNT(*) FROM dsi.ValidationIssue i WHERE i.WorksheetId = w.WorksheetId AND i.IsResolved = 0) AS OpenIssues,
                   sf.FileName, w.PageNo, w.CreatedAt
            FROM dsi.Worksheet w JOIN dsi.ScanFile sf ON sf.ScanFileId = w.ScanFileId
            ORDER BY w.CreatedAt DESC
            """, new { top }, cancellationToken: ct));
        return rows.Select(r => r.ToDto()).ToList();
    }

    private static string? Trim(string? s) => s is null ? null : s.Length > 400 ? s[..400] : s;

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
