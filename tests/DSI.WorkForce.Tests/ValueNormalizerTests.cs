using DSI.WorkForce.Core.Model;
using DSI.WorkForce.Core.Normalization;
using DSI.WorkForce.Core.Templates;

namespace DSI.WorkForce.Tests;

public class ValueNormalizerTests
{
    private static readonly DateOnly ScanDay = new(2026, 11, 20);

    [Fact]
    public void Date_written_as_yy_mm_dd_on_the_sheet_is_read_as_17_Nov_2026()
    {
        var f = new FieldDefinition { Key = "WORK_DATE", Type = FieldDataType.Date, DateFormats = ["yy/M/d", "d/M/yy", "d/M/yyyy"] };
        var v = ValueNormalizer.Normalize(f, 0, "26/11/17", 0.95m, ValueSource.Ocr, ScanDay);
        Assert.Equal(new DateOnly(2026, 11, 17), v.ValueDate);
        Assert.True(v.Confidence > 0.9m);
    }

    [Theory]
    [InlineData("26.11.17")]
    [InlineData("26-11-17")]
    [InlineData("26 / 11 / 17")]
    public void Date_separators_are_unified(string raw)
    {
        var f = new FieldDefinition { Key = "D", Type = FieldDataType.Date, DateFormats = ["yy/M/d"] };
        Assert.Equal(new DateOnly(2026, 11, 17), ValueNormalizer.Normalize(f, 0, raw, 1m, ValueSource.Ocr, ScanDay).ValueDate);
    }

    [Fact]
    public void Date_far_from_scan_date_gets_low_confidence()
    {
        var f = new FieldDefinition { Key = "D", Type = FieldDataType.Date, DateFormats = ["d/M/yyyy"] };
        var v = ValueNormalizer.Normalize(f, 0, "01/01/2025", 1m, ValueSource.Ocr, ScanDay);
        Assert.True(v.Confidence <= 0.4m);
    }

    [Theory]
    [InlineData("1234", 1234, false)]
    [InlineData("S2I4", 5214, true)]   // classic handwriting confusions
    [InlineData("12 34", 1234, false)]
    [InlineData("4O2", 402, true)]
    [InlineData("S.21.4", 5214, true)]   // pen dots between digits
    [InlineData("K.02", 402, true)]      // open-top 4 read as K (seen on the sample sheet)
    public void Integers_fix_letter_digit_confusions(string raw, int expected, bool lowered)
    {
        var f = new FieldDefinition { Key = "EPF_NO", Type = FieldDataType.Integer };
        var v = ValueNormalizer.Normalize(f, 0, raw, 0.99m, ValueSource.Ocr, ScanDay);
        Assert.Equal(expected, v.ValueNumber);
        Assert.Equal(lowered, v.Confidence < 0.99m);
    }

    [Fact]
    public void Unreadable_number_has_zero_confidence_and_keeps_text()
    {
        var f = new FieldDefinition { Key = "N", Type = FieldDataType.Integer };
        var v = ValueNormalizer.Normalize(f, 0, "abc#", 0.9m, ValueSource.Ocr, ScanDay);
        Assert.Null(v.ValueNumber);
        Assert.Equal(0m, v.Confidence);
        Assert.Equal("abc#", v.ValueText);
    }

    [Theory]
    [InlineData("2/10", "2-10")]
    [InlineData("2 - 10", "2-10")]
    [InlineData("6-2", "6-2")]
    [InlineData("2110", "2-10")]   // slash read as "1"
    public void Shift_snaps_to_allowed_values(string raw, string expected)
    {
        var f = new FieldDefinition { Key = "SHIFT", Type = FieldDataType.Text, AllowedValues = ["6-2", "2-10", "10-6"] };
        Assert.Equal(expected, ValueNormalizer.Normalize(f, 0, raw, 1m, ValueSource.Ocr, ScanDay).ValueText);
    }

    [Fact]
    public void Out_of_range_number_is_flagged_by_low_confidence()
    {
        var f = new FieldDefinition { Key = "T", Type = FieldDataType.Integer, Min = 0, Max = 5000 };
        Assert.True(ValueNormalizer.Normalize(f, 0, "99999", 1m, ValueSource.Ocr, ScanDay).Confidence <= 0.3m);
    }

    [Theory]
    [InlineData("4.45", "04:45")]
    [InlineData("14:00", "14:00")]
    [InlineData("9", "09:00")]
    public void Times_are_normalized(string raw, string expected) => Assert.Equal(expected, ValueNormalizer.NormalizeTime(raw));
}
