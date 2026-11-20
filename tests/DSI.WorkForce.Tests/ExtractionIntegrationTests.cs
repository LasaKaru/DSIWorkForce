using DSI.WorkForce.Core.Matching;
using DSI.WorkForce.Core.Model;
using DSI.WorkForce.Extraction;
using DSI.WorkForce.Extraction.Imaging;
using DSI.WorkForce.Extraction.Recognition;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace DSI.WorkForce.Tests;

/// <summary>
/// Runs the real OpenCV pipeline on a scanned PC-011-A. Point DSI_SAMPLE_PDF at a scan to enable it
/// (sample scans contain employee data, so they are not committed to the repository).
/// </summary>
public class ExtractionIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Sample_sheet_ticks_add_up_to_total_production()
    {
        var path = Environment.GetEnvironmentVariable("DSI_SAMPLE_PDF");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            output.WriteLine("DSI_SAMPLE_PDF not set - skipped.");
            return;
        }

        var template = TestData.PressTemplate();
        var extractor = new FormExtractor(new ManualKeyingRecognizer(), Options.Create(new ExtractionOptions()), NullLogger<FormExtractor>.Instance);
        var pdf = await File.ReadAllBytesAsync(path);
        using var page = PdfPageRenderer.RenderPage(pdf, 0, template.Alignment.RenderDpi);

        var ws = await extractor.ExtractAsync(page, 1, template, MasterDataSnapshot.Empty, new DateOnly(2026, 11, 20), default);
        foreach (var v in ws.Values.Where(v => !v.IsEmpty)) output.WriteLine(v.ToString());

        Assert.True(ws.AlignmentOk);
        var ticks = ws.GetRows("HOURLY_UNITS").Sum(v => int.Parse(v.RawText!));
        Assert.Equal(67, ticks);                                              // counted by hand on the sample
        Assert.Equal(402m, ws.GetRows("HOURLY_UNITS").Sum(v => v.ValueNumber)); // = Total Production box
        Assert.All(ws.Values.Where(v => v.FieldKey.StartsWith("CHK_")), v => Assert.True(v.ValueBool));
        Assert.True(ws.Get("OPERATOR_SIGNED")!.ValueBool);
        Assert.Equal(WorksheetStatus.NeedsReview, ws.Status);   // handwriting not read in manual mode
    }
}
