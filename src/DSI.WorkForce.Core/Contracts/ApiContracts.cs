using DSI.WorkForce.Core.Templates;

namespace DSI.WorkForce.Core.Contracts;

// DTOs shared by the ASP.NET Core API and the WPF desktop client.

public sealed record DashboardDto(
    int PendingFiles, int ProcessingFiles, int FailedFiles,
    int SheetsToday, int AutoApprovedToday, int NeedsReviewTotal, int VerifiedToday,
    DateTime? LastWorkerHeartbeatUtc);

public sealed record WorksheetSummaryDto(
    long WorksheetId, string TemplateCode, DateOnly? WorkDate, string? Shift, string? ResourceNo,
    string? EpfNo, string? ItemCode, string Status, decimal MinConfidence, int OpenIssues,
    string FileName, int PageNo, DateTime CreatedAt);

public sealed record WorksheetValueDto(
    string FieldKey, int RowIndex, string Label, string? Group, string DataType,
    string? RawText, string? Value, decimal Confidence, string Source, bool IsCorrected,
    Zone? Zone,
    int GridRow = 0,          // grid cells: hour row (1..8)
    int GridColumn = 0,       // grid cells: cell in that row (1..12); 0 = not a grid cell
    bool IsComputed = false); // calculated (e.g. hourly sum) - read-only

public sealed record ValidationIssueDto(string? FieldKey, int RowIndex, string Severity, string Code, string Message);

public sealed record WorksheetDetailDto(
    WorksheetSummaryDto Summary,
    IReadOnlyList<WorksheetValueDto> Values,
    IReadOnlyList<ValidationIssueDto> Issues,
    byte[]? RowVersion);

public sealed record ValueCorrectionDto(string FieldKey, int RowIndex, string? Value);

public sealed record SaveCorrectionsRequest(
    IReadOnlyList<ValueCorrectionDto> Corrections,
    bool MarkVerified,
    byte[]? RowVersion);

public sealed record ReportQuery(string ReportCode, DateOnly From, DateOnly To, string? Shift, string? ResourceNo, string? EpfNo);

public sealed record ReportColumnInfoDto(string Header, string? Format);

public sealed record ReportResultDto(
    string ReportCode, string Title,
    IReadOnlyList<ReportColumnInfoDto> Columns,
    IReadOnlyList<object?[]> Rows);

public sealed record UploadResultDto(string FileName, bool Accepted, string Message);
