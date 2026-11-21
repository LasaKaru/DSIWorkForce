using DSI.WorkForce.Core.Matching;
using DSI.WorkForce.Extraction;
using DSI.WorkForce.Extraction.Imaging;
using DSI.WorkForce.Extraction.Recognition.Digits;
using DSI.WorkForce.Extraction.Recognition.Local;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace DSI.WorkForce.Tests;

/// <summary>
/// Second real scan: coloured scanner streaks, beige paper on a white scan background, form smaller on the page,
/// numbers written in the hourly grid. Opt-in: DSI_SAMPLE2_PDF=path and DSI_TEST_LOCAL_OCR=1
/// (on Linux also build with -p:DsiLinuxOcr=true). Scans contain employee data and are not committed.
/// </summary>
public class StreakyScanIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Streaky_scan_aligns_and_hourly_digits_are_read()
    {
        var path = Environment.GetEnvironmentVariable("DSI_SAMPLE2_PDF");
        if (Environment.GetEnvironmentVariable("DSI_TEST_LOCAL_OCR") != "1" || string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            output.WriteLine("Set DSI_SAMPLE2_PDF and DSI_TEST_LOCAL_OCR=1 to run - skipped.");
            return;
        }

        var options = Options.Create(new ExtractionOptions());
        using var recognizer = new PaddleLocalRecognizer(options, NullLogger<PaddleLocalRecognizer>.Instance);
        using var digits = new DigitCellReader();
        var extractor = new FormExtractor(recognizer, options, NullLogger<FormExtractor>.Instance, digits);
        var template = TestData.PressTemplate();
        using var page = PdfPageRenderer.RenderPage(await File.ReadAllBytesAsync(path), 0, template.Alignment.RenderDpi);
        var ws = await extractor.ExtractAsync(page, 1, template, MasterDataSnapshot.Empty, new DateOnly(2026, 11, 21), default);

        Assert.True(ws.AlignmentOk);

        string[] truth = ["87654321", "987654321", "___54321", "987654321", "87654321", "987654321", "87654321", "987654321"];
        var cells = template.Find("HOURLY_CELL")!;
        int correct = 0, total = 0, falseValues = 0;
        for (var r = 1; r <= 8; r++)
            for (var c = 1; c <= 12; c++)
            {
                var expected = c <= truth[r - 1].Length ? truth[r - 1][c - 1] : '_';
                var got = ws.Get("HOURLY_CELL", cells.CellIndex(r, c))?.ValueText;
                if (expected == '_') { if (got is not null) falseValues++; continue; }
                total++;
                if (got == expected.ToString()) correct++;
            }
        output.WriteLine($"hourly digits {correct}/{total}, values in empty cells {falseValues}");
        Assert.True(correct >= 60, $"only {correct}/{total} hourly digits correct");   // measured: 63/65
        Assert.Equal(0, falseValues);                                                    // streaks must not become numbers
        Assert.Equal("390", ws.Get("TOTAL_PRODUCTION")?.ValueText);
        Assert.Equal("6-2", ws.Get("SHIFT")?.ValueText);
    }
}
