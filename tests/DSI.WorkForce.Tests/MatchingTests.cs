using DSI.WorkForce.Core.Matching;
using DSI.WorkForce.Core.Model;

namespace DSI.WorkForce.Tests;

public class MatchingTests
{
    private static MasterDataSnapshot Master() => new(
        [new EmployeeMaster("1234", "Sample Operator")],
        [new MachineMaster("P 24", "Press 24", 6m)],
        [
            new ItemMaster("TYR-00094", "29 2.10 SRI 94 Full Black", 26m),
            new ItemMaster("TYR-00068", "29 2.10 SRI 68 Full Black", 26m),
            new ItemMaster("TYR-01200", "16 2.50 SRI 12 Grey", 30m),
        ],
        []);

    [Fact]
    public void Full_black_and_FB_are_the_same_item()
    {
        Assert.Equal(FuzzyMatcher.CanonicalItem("29 2.10 SRI 94 Full Black"), FuzzyMatcher.CanonicalItem("29 2.10 sri 94 F/B"));
    }

    [Fact]
    public void Noisy_handwritten_item_is_snapped_to_master()
    {
        var t = TestData.PressTemplate();
        var ws = new WorksheetExtraction { TemplateCode = t.TemplateCode, PageNo = 1 };
        ws.Values.Add(new ExtractedValue { FieldKey = "ITEM_DESCRIPTION", RawText = "29x2.10 SRI 94 F/B", ValueText = "29x2.10 SRI 94 F/B", Confidence = 0.8m });
        ws.Values.Add(new ExtractedValue { FieldKey = "RESOURCE_NO", ValueText = "P-24", Confidence = 0.95m });

        LookupResolver.Apply(ws, t, Master());

        Assert.Equal("TYR-00094", ws.ItemCode);
        Assert.Equal("29 2.10 SRI 94 Full Black", ws.Get("ITEM_DESCRIPTION")!.ValueText);
        Assert.Equal("P 24", ws.ResourceNo);   // "P-24" written on paper -> master spelling
    }

    [Fact]
    public void Unknown_epf_raises_error()
    {
        var t = TestData.PressTemplate();
        var ws = new WorksheetExtraction { TemplateCode = t.TemplateCode, PageNo = 1 };
        ws.Values.Add(new ExtractedValue { FieldKey = "EPF_NO", ValueText = "9999", ValueNumber = 9999, Confidence = 0.99m });
        LookupResolver.Apply(ws, t, Master());
        Assert.Contains(ws.Issues, i => i.Code == "EPF_NOT_FOUND");
    }

    [Fact]
    public void Machine_units_per_tick_overrides_template_default()
    {
        var t = TestData.PressTemplate();
        var ws = new WorksheetExtraction { TemplateCode = t.TemplateCode, PageNo = 1 };
        ws.Values.Add(new ExtractedValue { FieldKey = "RESOURCE_NO", ValueText = "P24", Confidence = 0.95m });
        ws.Values.Add(new ExtractedValue { FieldKey = "HOURLY_UNITS", RowIndex = 1, RawText = "8", ValueNumber = 8, Confidence = 0.99m });
        var master = new MasterDataSnapshot([], [new MachineMaster("P 24", null, 4m)], [], []);
        LookupResolver.Apply(ws, t, master);
        Assert.Equal(32m, ws.Get("HOURLY_UNITS", 1)!.ValueNumber);
    }

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("", "abc", 3)]
    [InlineData("same", "same", 0)]
    public void Levenshtein(string a, string b, int d) => Assert.Equal(d, FuzzyMatcher.Levenshtein(a, b));
}
