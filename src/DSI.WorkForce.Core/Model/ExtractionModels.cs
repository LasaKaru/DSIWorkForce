namespace DSI.WorkForce.Core.Model;

/// <summary>One value read from one sheet. RowIndex = 0 for single fields, 1..n for table rows (e.g. hour slot).</summary>
public sealed class ExtractedValue
{
    public required string FieldKey { get; init; }
    public int RowIndex { get; init; }
    /// <summary>Exactly what the OCR / OMR produced, before cleaning (kept for audit &amp; model retraining).</summary>
    public string? RawText { get; set; }
    public string? ValueText { get; set; }
    public decimal? ValueNumber { get; set; }
    public DateOnly? ValueDate { get; set; }
    public bool? ValueBool { get; set; }
    /// <summary>0..1</summary>
    public decimal Confidence { get; set; }
    public ValueSource Source { get; set; }

    public bool IsEmpty => ValueText is null && ValueNumber is null && ValueDate is null && ValueBool is null;

    public override string ToString() =>
        $"{FieldKey}[{RowIndex}] = {ValueText ?? ValueNumber?.ToString() ?? ValueDate?.ToString("yyyy-MM-dd") ?? ValueBool?.ToString() ?? "(empty)"} ({Confidence:P0}, {Source})";
}

public sealed record ValidationIssue(string? FieldKey, int RowIndex, IssueSeverity Severity, string Code, string Message);

/// <summary>Everything extracted from one page (= one worksheet).</summary>
public sealed class WorksheetExtraction
{
    public required string TemplateCode { get; init; }
    public int TemplateVersion { get; init; }
    public int PageNo { get; init; }
    public List<ExtractedValue> Values { get; } = new();
    public List<ValidationIssue> Issues { get; } = new();
    public bool AlignmentOk { get; set; } = true;

    /// <summary>Aligned page image (JPEG) kept for the review screen.</summary>
    public byte[]? AlignedImageJpeg { get; set; }

    public WorksheetStatus Status { get; set; } = WorksheetStatus.NeedsReview;

    // Promoted header values (copied from fields with promoteTo)
    public DateOnly? WorkDate { get; set; }
    public string? Shift { get; set; }
    public string? ResourceNo { get; set; }
    public string? EpfNo { get; set; }
    public string? ItemCode { get; set; }

    public decimal MinConfidence => Values.Count == 0 ? 0 : Values.Min(v => v.Confidence);

    public ExtractedValue? Get(string key, int row = 0) =>
        Values.FirstOrDefault(v => v.RowIndex == row && string.Equals(v.FieldKey, key, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ExtractedValue> GetRows(string key) =>
        Values.Where(v => string.Equals(v.FieldKey, key, StringComparison.OrdinalIgnoreCase) && v.RowIndex > 0).OrderBy(v => v.RowIndex);
}
