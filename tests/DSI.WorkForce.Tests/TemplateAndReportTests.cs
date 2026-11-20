using DSI.WorkForce.Core.Model;
using DSI.WorkForce.Core.Reporting;
using DSI.WorkForce.Core.Templates;

namespace DSI.WorkForce.Tests;

public class TemplateAndReportTests
{
    [Fact]
    public void Press_template_is_valid()
    {
        var t = TestData.PressTemplate();
        Assert.Equal("PC-011-A", t.TemplateCode);
        Assert.Empty(TemplateSerializer.Validate(t));
        Assert.Equal(14, t.Fields.Count(f => f.Group == "Machine Checklist"));
        var cells = t.Find("HOURLY_CELL")!;
        Assert.True(cells.IsGrid);
        Assert.Equal(96, cells.RowIndexes().Count());          // 8 hours x 12 handwritten numbers
        Assert.Equal(15, cells.CellIndex(2, 3));               // stored RowIndex of hour 2, cell 3
        Assert.Equal((2, 3), cells.CellOf(15));
        Assert.Equal(ExtractorKind.Computed, t.Find("HOURLY_UNITS")!.Extractor);
        Assert.NotNull(t.Find("HOURLY_REJECTION"));
    }

    [Fact]
    public void Template_rejects_bad_keys_and_zones()
    {
        var t = new FormTemplateDefinition { TemplateCode = "X" };
        t.Fields.Add(new FieldDefinition { Key = "bad key", Zone = new Zone(0.9, 0.9, 0.5, 0.5) });
        var errors = TemplateSerializer.Validate(t);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Daily_efficiency_view_matches_excel_layout()
    {
        var t = TestData.PressTemplate();
        var report = TestData.DailyEfficiency();
        var sql = ReportViewSqlBuilder.Build(report, TestData.FieldMap(t));

        Assert.StartsWith("--", sql.ViewSql);
        Assert.Contains("CREATE OR ALTER VIEW rpt.[vw_DailyEfficiency]", sql.ViewSql);
        Assert.Contains("CREATE OR ALTER FUNCTION rpt.[fn_DailyEfficiency] (@From DATE, @To DATE)", sql.FunctionSql);
        Assert.Contains("w.WorkDate BETWEEN @From AND @To", sql.FunctionSql);
        Assert.DoesNotContain("@From", sql.ViewSql);

        // Excel headers, in Excel order
        string[] excel = ["Resource No", "Item Code", "Item Description", "EPF No", "Date", "Shift", "Start time", "End Time",
            "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "No of Units produced", "Standard Rate per Hour", "Efficiency", "Reason"];
        var outer = sql.ViewSql[sql.ViewSql.LastIndexOf("SELECT", StringComparison.Ordinal)..];
        var positions = excel.Select(h => outer.IndexOf($"AS [{h}]", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.OrderBy(p => p), positions);

        // One row per hour, like the Excel: Efficiency = units of the hour / standard rate per hour (=V/W)
        Assert.Contains("CROSS JOIN (VALUES (1), (2), (3), (4), (5), (6), (7), (8)) AS r(n)", sql.ViewSql);
        Assert.Contains("HAVING COUNT(CASE WHEN", sql.ViewSql);                       // empty hours are skipped
        Assert.Contains("v.RowIndex = (r.n - 1) * 12 + 3", sql.ViewSql);              // column "3" = cell 3 of this hour
        Assert.Contains("DATEADD(HOUR, r.n - 1, sh.StartTime)", sql.ViewSql);         // Start time of this hour
        Assert.Contains("b.[Units] / NULLIF(b.[StdRate], 0) AS [Efficiency]", sql.ViewSql);
        Assert.Contains("AS [Rejection]", outer);
        Assert.Contains("AS [Sheet ID]", sql.ViewSql);
        Assert.Contains("AS [Row No]", sql.ViewSql);
    }

    [Fact]
    public void Adding_a_column_is_just_metadata()
    {
        var t = TestData.PressTemplate();
        var report = TestData.DailyEfficiency();
        report.Columns.Add(new ReportColumnDefinition
        {
            Order = 999, Key = "CuringTemp", Header = "Curing Temp", SourceType = ReportSourceType.Field, Source = "CURING_TEMP",
        });
        var sql = ReportViewSqlBuilder.Build(report, TestData.FieldMap(t));
        Assert.Contains("AS [Curing Temp]", sql.ViewSql);
    }

    [Fact]
    public void Shift_summary_is_one_row_per_sheet()
    {
        var sql = ReportViewSqlBuilder.Build(TestData.Report("ShiftSummary"), TestData.FieldMap(TestData.PressTemplate()));
        Assert.DoesNotContain("CROSS JOIN", sql.ViewSql);
        Assert.Contains("v.RowIndex = 3", sql.ViewSql);   // "Hour 3" column
        Assert.Contains("b.[Units] / NULLIF(b.[StdRate] * b.[HoursWorked], 0) AS [Efficiency]", sql.ViewSql);
        Assert.DoesNotContain("AS [Hours Worked]", sql.ViewSql);   // hidden helper column
    }

    [Fact]
    public void Grid_column_needs_a_cell_and_hour_times_need_a_per_row_report()
    {
        var t = TestData.PressTemplate();
        var report = TestData.Report("ShiftSummary");
        report.Columns.Add(new ReportColumnDefinition { Order = 900, Key = "X", Header = "X", SourceType = ReportSourceType.Field, Source = "HOURLY_CELL", RowIndex = 1 });
        report.Columns.Add(new ReportColumnDefinition { Order = 901, Key = "Y", Header = "Y", SourceType = ReportSourceType.Lookup, Source = "Shift.HourStart" });
        var errors = ReportViewSqlBuilder.Validate(report, TestData.FieldMap(t));
        Assert.Contains(errors, e => e.Contains("choose a cell"));
        Assert.Contains(errors, e => e.Contains("per-row report"));
    }

    [Theory]
    [InlineData("{Units}; DROP TABLE dsi.Worksheet")]
    [InlineData("{Units} -- comment")]
    [InlineData("(SELECT 1)")]
    [InlineData("{NotAColumn} * 2")]
    [InlineData("'x'")]
    public void Expressions_cannot_inject_sql(string expression)
    {
        var t = TestData.PressTemplate();
        var report = TestData.DailyEfficiency();
        report.Columns.Add(new ReportColumnDefinition { Order = 999, Key = "Bad", Header = "Bad", SourceType = ReportSourceType.Expression, Expression = expression });
        Assert.NotEmpty(ReportViewSqlBuilder.Validate(report, TestData.FieldMap(t)));
        Assert.Throws<InvalidOperationException>(() => ReportViewSqlBuilder.Build(report, TestData.FieldMap(t)));
    }

    [Fact]
    public void Header_names_are_bracket_escaped()
    {
        Assert.Equal("[a]]b]", ReportViewSqlBuilder.Q("a]b"));
    }
}
