using DSI.WorkForce.Core.Matching;
using DSI.WorkForce.Extraction;
using DSI.WorkForce.Extraction.Imaging;
using DSI.WorkForce.Extraction.Recognition.Local;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace DSI.WorkForce.Tests;

/// <summary>
/// Real handwriting OCR (PaddleOCR PP-OCRv5) on the sample sheet. Opt-in because it needs the native Paddle engine:
/// set DSI_SAMPLE_PDF and DSI_TEST_LOCAL_OCR=1 (on Linux also build with -p:DsiLinuxOcr=true).
/// </summary>
public class LocalOcrIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Local_ocr_reads_the_key_handwritten_values_of_the_sample()
    {
        var path = Environment.GetEnvironmentVariable("DSI_SAMPLE_PDF");
        if (Environment.GetEnvironmentVariable("DSI_TEST_LOCAL_OCR") != "1" || string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            output.WriteLine("Set DSI_SAMPLE_PDF and DSI_TEST_LOCAL_OCR=1 to run - skipped.");
            return;
        }

        var options = Options.Create(new ExtractionOptions());
        using var recognizer = new PaddleLocalRecognizer(options, NullLogger<PaddleLocalRecognizer>.Instance);
        var extractor = new FormExtractor(recognizer, options, NullLogger<FormExtractor>.Instance);
        var template = TestData.PressTemplate();
        using var page = PdfPageRenderer.RenderPage(await File.ReadAllBytesAsync(path), 0, template.Alignment.RenderDpi);
        var ws = await extractor.ExtractAsync(page, 1, template, MasterDataSnapshot.Empty, new DateOnly(2026, 11, 20), default);

        var expected = new Dictionary<(string, int), string>
        {
            [("RESOURCE_NO", 0)] = "P-24", [("EPF_NO", 0)] = "5916", [("SHIFT", 0)] = "2-10", [("TARGET_QTY", 0)] = "390",
            [("TOTAL_PRODUCTION", 0)] = "402", [("STEAM_PRESSURE", 0)] = "15-0150", [("MOULD_LOG_TIME", 4)] = "4.45",
            [("MOULD_LOG_VALUE", 1)] = "24", [("MOULD_LOG_VALUE", 2)] = "23", [("MOULD_LOG_VALUE", 3)] = "22",
            [("MOULD_LOG_VALUE", 4)] = "21", [("MOULD_LOG_VALUE", 5)] = "20", [("MOULD_LOG_VALUE", 6)] = "19",
        };
        var correct = 0;
        foreach (var ((key, row), want) in expected)
        {
            var got = ws.Get(key, row)?.ValueText;
            output.WriteLine($"{(got == want ? "OK" : "XX")} {key}[{row}] = '{got}' (expected '{want}')");
            if (got == want) correct++;
        }
        // Measured: 13 / 13 of these on the sample. Allow a little slack for engine/version differences.
        Assert.True(correct >= 11, $"Only {correct} of {expected.Count} key values read correctly.");
    }
}
