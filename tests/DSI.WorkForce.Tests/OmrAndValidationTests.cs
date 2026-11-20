using DSI.WorkForce.Core.Model;
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

    private static WorksheetExtraction Sheet(decimal total)
    {
        var ws = new WorksheetExtraction { TemplateCode = "PC-011-A", PageNo = 1 };
        int[] ticks = [8, 9, 8, 8, 9, 8, 8, 9];   // = 67 ticks on the sample sheet
        for (var h = 1; h <= 8; h++)
            ws.Values.Add(new ExtractedValue { FieldKey = "HOURLY_UNITS", RowIndex = h, RawText = ticks[h - 1].ToString(), ValueNumber = ticks[h - 1] * 6, Confidence = 0.95m });
        ws.Values.Add(new ExtractedValue { FieldKey = "TOTAL_PRODUCTION", ValueNumber = total, ValueText = total.ToString(), Confidence = 0.8m });
        return ws;
    }

    [Fact]
    public void Ticks_times_six_matching_written_total_boosts_confidence()
    {
        var t = TestData.PressTemplate();
        t.Fields.RemoveAll(f => f.Required);
        var ws = Sheet(402);                  // 67 x 6 = 402, the value written in the Total Production box
        WorksheetValidator.Validate(ws, t);
        Assert.DoesNotContain(ws.Issues, i => i.Code == "TOTAL_MISMATCH");
        Assert.Equal(0.98m, ws.Get("TOTAL_PRODUCTION")!.Confidence);
        Assert.Equal(WorksheetStatus.AutoApproved, ws.Status);
    }

    [Fact]
    public void Total_mismatch_sends_sheet_to_review()
    {
        var t = TestData.PressTemplate();
        t.Fields.RemoveAll(f => f.Required);
        var ws = Sheet(420);
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
