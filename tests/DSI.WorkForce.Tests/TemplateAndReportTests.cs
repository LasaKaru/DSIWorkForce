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
        var hourly = t.Find("HOURLY_UNITS")!;
        Assert.Equal(8, hourly.Rows);
        Assert.Equal(12, hourly.Columns);
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

        Assert.Contains("b.[Units] / NULLIF(b.[StdRate] * b.[HoursWorked], 0) AS [Efficiency]", sql.ViewSql);
        Assert.Contains("WITHIN GROUP (ORDER BY v.RowIndex)", sql.ViewSql);
        Assert.Contains("LEFT JOIN dsi.ShiftMaster sh", sql.ViewSql);
        Assert.Contains("AS [Sheet ID]", sql.ViewSql);
        Assert.DoesNotContain("AS [Hours Worked]", sql.ViewSql);   // hidden helper column
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
