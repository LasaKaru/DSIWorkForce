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
        var pdf = await File.ReadAllBytesAsync(path);
        using var page = PdfPageRenderer.RenderPage(pdf, 0, template.Alignment.RenderDpi);

        // The sample scan still has ticks in the hourly grid; the stub recognizer "reads" a 6 in every filled
        // cell (what operators will now write) and 402 in the Total Production box.
        var extractor = new FormExtractor(new StubRecognizer(), Options.Create(new ExtractionOptions()), NullLogger<FormExtractor>.Instance);
        var ws = await extractor.ExtractAsync(page, 1, template, MasterDataSnapshot.Empty, new DateOnly(2026, 11, 20), default);
        foreach (var v in ws.Values.Where(v => !v.IsEmpty && v.FieldKey != "HOURLY_CELL")) output.WriteLine(v.ToString());

        Assert.True(ws.AlignmentOk);
        var filledPerHour = Enumerable.Range(1, 8).Select(h => ws.GetRows("HOURLY_CELL").Count(c => !c.IsEmpty && template.Find("HOURLY_CELL")!.CellOf(c.RowIndex).Row == h)).ToArray();
        Assert.Equal([8, 9, 8, 8, 9, 8, 8, 9], filledPerHour);                 // blank vs written cells, counted by hand
        Assert.Equal([48m, 54m, 48m, 48m, 54m, 48m, 48m, 54m], ws.GetRows("HOURLY_UNITS").Select(v => v.ValueNumber!.Value));
        Assert.DoesNotContain(ws.Issues, i => i.Code == "TOTAL_MISMATCH");     // 402 = 402
        Assert.All(ws.Values.Where(v => v.FieldKey.StartsWith("CHK_")), v => Assert.True(v.ValueBool));
        Assert.True(ws.Get("OPERATOR_SIGNED")!.ValueBool);
    }

    private sealed class StubRecognizer : IHandwritingRecognizer
    {
        public string Name => "Stub";
        public Task<IReadOnlyList<RecognitionResult>> RecognizeAsync(OpenCvSharp.Mat alignedGray, IReadOnlyList<RecognitionRequest> requests, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RecognitionResult>>(requests.Select(r => new RecognitionResult(r.FieldKey, r.RowIndex,
                r.FieldKey switch { "HOURLY_CELL" => "6", "TOTAL_PRODUCTION" => "402", _ => null }, 0.93m)).ToList());
    }
}
