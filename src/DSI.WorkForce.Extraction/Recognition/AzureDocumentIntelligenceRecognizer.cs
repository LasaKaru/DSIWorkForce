using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCvSharp;

namespace DSI.WorkForce.Extraction.Recognition;

/// <summary>
/// Sends the aligned page once to Azure AI Document Intelligence (prebuilt-read or a custom model) and
/// assigns each recognised word to the zone that contains its centre. One API call per sheet.
/// </summary>
public sealed class AzureDocumentIntelligenceRecognizer : IHandwritingRecognizer
{
    private readonly DocumentIntelligenceClient _client;
    private readonly AzureDocumentIntelligenceOptions _options;
    private readonly ILogger<AzureDocumentIntelligenceRecognizer> _log;

    public string Name => "AzureDocumentIntelligence";

    public AzureDocumentIntelligenceRecognizer(IOptions<ExtractionOptions> options, ILogger<AzureDocumentIntelligenceRecognizer> log)
    {
        _options = options.Value.Azure;
        _log = log;
        if (string.IsNullOrWhiteSpace(_options.Endpoint) || string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("Extraction:Azure:Endpoint and ApiKey must be configured for the AzureDocumentIntelligence recognizer.");
        _client = new DocumentIntelligenceClient(new Uri(_options.Endpoint), new AzureKeyCredential(_options.ApiKey));
    }

    public async Task<IReadOnlyList<RecognitionResult>> RecognizeAsync(Mat alignedGray, IReadOnlyList<RecognitionRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0) return [];
        Cv2.ImEncode(".png", alignedGray, out var png);

        var op = await _client.AnalyzeDocumentAsync(WaitUntil.Completed, _options.ModelId, BinaryData.FromBytes(png), ct);
        var page = op.Value.Pages.FirstOrDefault();
        if (page is null) return requests.Select(r => new RecognitionResult(r.FieldKey, r.RowIndex, null, 0m)).ToList();

        double pw = page.Width ?? alignedGray.Width, ph = page.Height ?? alignedGray.Height;
        var buckets = requests.ToDictionary(r => r, _ => new List<DocumentWord>());
        foreach (var word in page.Words)
        {
            var poly = word.Polygon;
            if (poly is null || poly.Count < 8) continue;
            double cx = 0, cy = 0;
            for (var i = 0; i < poly.Count; i += 2) { cx += poly[i]; cy += poly[i + 1]; }
            cx = cx / (poly.Count / 2) / pw;
            cy = cy / (poly.Count / 2) / ph;
            var target = requests.FirstOrDefault(r => cx >= r.Zone.X && cx <= r.Zone.X + r.Zone.W && cy >= r.Zone.Y && cy <= r.Zone.Y + r.Zone.H);
            if (target is not null) buckets[target].Add(word);
        }

        _log.LogDebug("Azure DI returned {Words} words for {Zones} zones", page.Words.Count, requests.Count);
        return buckets.Select(kv => kv.Value.Count == 0
                ? new RecognitionResult(kv.Key.FieldKey, kv.Key.RowIndex, null, 0.3m)   // ink present but nothing read
                : new RecognitionResult(kv.Key.FieldKey, kv.Key.RowIndex,
                    string.Join(' ', kv.Value.Select(w => w.Content)),
                    (decimal)kv.Value.Min(w => w.Confidence)))
            .ToList();
    }
}
