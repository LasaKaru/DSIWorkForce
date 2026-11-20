using DSI.WorkForce.Core.Model;
using DSI.WorkForce.Core.Normalization;
using DSI.WorkForce.Core.Omr;
using DSI.WorkForce.Core.Validation;

namespace DSI.WorkForce.Tests;

public class OmrAndValidationTests
{
    [Theory]
    [InlineData(0.000, MarkState.Empty)]
    [InlineData(0.060, MarkState.Marked)]
    [InlineData(0.016, MarkState.Uncertain)]
    public void Classify_ink_ratio(double ratio, MarkState expected) =>
        Assert.Equal(expected, OmrClassifier.Classify(ratio, 0.015, 0.008).State);

    [Fact]
    public void Tally_counts_marked_cells()
    {
        // Row 1 of the sample sheet (ink ratios measured from the scan): 8 ticks, 4 empty cells.
        var ratios = new[] { 0.067, 0.081, 0.079, 0.045, 0.089, 0.183, 0.083, 0.057, 0, 0, 0, 0 };
        var (ticks, conf, uncertain) = OmrClassifier.CountTally(ratios, 0.015, 0.008);
        Assert.Equal(8, ticks);
        Assert.Equal(0, uncertain);
        Assert.True(conf > 0.9m);
    }

    /// <summary>Hourly grid as on the sample sheet: 8, 9, 8, 8, 9, 8, 8, 9 filled cells per hour, each holding the number 6.</summary>
    private static WorksheetExtraction Sheet(decimal total, string cellValue = "6")
    {
        var t = TestData.PressTemplate();
        var cellsField = t.Find("HOURLY_CELL")!;
        var ws = new WorksheetExtraction { TemplateCode = "PC-011-A", PageNo = 1 };
        int[] filled = [8, 9, 8, 8, 9, 8, 8, 9];
        for (var h = 1; h <= 8; h++)
            for (var c = 1; c <= 12; c++)
            {
                var v = c <= filled[h - 1]
                    ? ValueNormalizer.Normalize(cellsField, cellsField.CellIndex(h, c), cellValue, 0.93m, ValueSource.Ocr, new DateOnly(2026, 11, 20))
                    : new ExtractedValue { FieldKey = "HOURLY_CELL", RowIndex = cellsField.CellIndex(h, c), Confidence = 0.99m };
                ws.Values.Add(v);
            }
        ws.Values.Add(new ExtractedValue { FieldKey = "TOTAL_PRODUCTION", ValueNumber = total, ValueText = total.ToString(), Confidence = 0.8m });
        ComputedFields.Apply(ws, t);
        return ws;
    }

    [Fact]
    public void Hourly_units_are_the_sum_of_the_numbers_in_each_hour_row()
    {
        var ws = Sheet(402);
        Assert.Equal([48m, 54m, 48m, 48m, 54m, 48m, 48m, 54m], ws.GetRows("HOURLY_UNITS").Select(v => v.ValueNumber!.Value));
        Assert.Equal(402m, ws.GetRows("HOURLY_UNITS").Sum(v => v.ValueNumber));
    }

    [Fact]
    public void Unread_cell_makes_the_hour_sum_unknown()
    {
        var ws = Sheet(402);
        var t = TestData.PressTemplate();
        var cell = ws.Get("HOURLY_CELL", t.Find("HOURLY_CELL")!.CellIndex(3, 2))!;
        cell.ValueNumber = null; cell.ValueText = null; cell.Confidence = 0m;   // handwriting not read yet
        ComputedFields.Apply(ws, t);
        Assert.Null(ws.Get("HOURLY_UNITS", 3)!.ValueNumber);
        Assert.Equal(54m, ws.Get("HOURLY_UNITS", 2)!.ValueNumber);
    }

    [Fact]
    public void Grid_total_matching_written_total_boosts_cells_and_total()
    {
        var t = TestData.PressTemplate();
        t.Fields.RemoveAll(f => f.Required);
        var ws = Sheet(402);                  // 67 cells x 6 = 402, the value written in the Total Production box
        WorksheetValidator.Validate(ws, t);
        Assert.DoesNotContain(ws.Issues, i => i.Code == "TOTAL_MISMATCH");
        Assert.Equal(0.98m, ws.Get("TOTAL_PRODUCTION")!.Confidence);
        Assert.All(ws.GetRows("HOURLY_CELL").Where(c => c.ValueNumber is not null), c => Assert.Equal(0.98m, c.Confidence));
        Assert.Equal(WorksheetStatus.AutoApproved, ws.Status);
    }

    [Fact]
    public void Total_mismatch_sends_sheet_to_review()
    {
        var t = TestData.PressTemplate();
        t.Fields.RemoveAll(f => f.Required);
        var ws = Sheet(402, cellValue: "5");   // one misread digit pattern -> sums no longer match the written total
        WorksheetValidator.Validate(ws, t);
        Assert.Contains(ws.Issues, i => i.Code == "TOTAL_MISMATCH");
        Assert.Equal(WorksheetStatus.NeedsReview, ws.Status);
    }

    [Fact]
    public void Missing_required_field_sends_sheet_to_review()
    {
        var t = TestData.PressTemplate();
        var ws = Sheet(402);
        WorksheetValidator.Validate(ws, t);
        Assert.Contains(ws.Issues, i => i.Code == "REQUIRED" && i.FieldKey == "EPF_NO");
        Assert.Equal(WorksheetStatus.NeedsReview, ws.Status);
    }
}
