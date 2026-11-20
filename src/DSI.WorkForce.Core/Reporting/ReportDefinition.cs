namespace DSI.WorkForce.Core.Reporting;

public enum ReportSourceType
{
    /// <summary>Indexed column of dsi.Worksheet (WorkDate, Shift, ResourceNo, EpfNo, ItemCode, Status, WorksheetId ...).</summary>
    Header = 0,
    /// <summary>One captured value: FieldKey + optional RowIndex (e.g. HOURLY_UNITS row 3 = hour 3).</summary>
    Field = 1,
    /// <summary>Aggregate over all rows of a field: SUM, MIN, MAX, AVG, COUNT, LIST.</summary>
    FieldAggregate = 2,
    /// <summary>Master data column: Item.StandardRatePerHour, Employee.Name, Shift.StartTime ...</summary>
    Lookup = 3,
    /// <summary>Formula over other columns: {Units} / NULLIF({StdRate} * {HoursWorked}, 0)</summary>
    Expression = 4,
}

/// <summary>A report = one SQL view (rpt.vw_&lt;Code&gt;) that looks like the Excel sheet.</summary>
public sealed class ReportDefinition
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    /// <summary>Template codes whose worksheets feed this report (empty = all).</summary>
    public List<string> TemplateCodes { get; set; } = new();
    /// <summary>When true the view only shows AutoApproved + Verified sheets.</summary>
    public bool OnlyApproved { get; set; }
    public List<ReportColumnDefinition> Columns { get; set; } = new();

    public string ViewName => $"vw_{Code}";
    public string FunctionName => $"fn_{Code}";
}

public sealed class ReportColumnDefinition
{
    public int Order { get; set; }
    /// <summary>Identifier used inside expressions, e.g. Units. Letters/digits/underscore.</summary>
    public string Key { get; set; } = "";
    /// <summary>Column title exactly as in Excel, e.g. "No of Units produced".</summary>
    public string Header { get; set; } = "";
    public ReportSourceType SourceType { get; set; }
    /// <summary>Header column name, FieldKey, or Lookup "Table.Column".</summary>
    public string? Source { get; set; }
    public int? RowIndex { get; set; }
    /// <summary>SUM | MIN | MAX | AVG | COUNT | LIST</summary>
    public string? Aggregate { get; set; }
    public string? Expression { get; set; }
    /// <summary>Excel / grid display format, e.g. "0.00%", "dd/MM/yyyy", "#,##0".</summary>
    public string? Format { get; set; }
    /// <summary>Hidden columns can be used by expressions without appearing in the report.</summary>
    public bool Visible { get; set; } = true;
}
