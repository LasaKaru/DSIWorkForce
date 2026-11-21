using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace DSI.WorkForce.Extraction.Recognition;

/// <summary>
/// Reads everything with the fast local engine first and sends only the boxes it is unsure about to a stronger
/// (paid / GPU) engine. Typical result: most boxes never leave the server, cost stays low.
/// </summary>
public sealed class CascadingRecognizer(IHandwritingRecognizer first, IHandwritingRecognizer second, decimal escalateBelow, ILogger log)
    : IHandwritingRecognizer, IWarmUp
{
    public void WarmUp(int instances) => (first as IWarmUp)?.WarmUp(instances);

    public string Name => $"{first.Name}+{second.Name}";

    public async Task<IReadOnlyList<RecognitionResult>> RecognizeAsync(Mat alignedGray, IReadOnlyList<RecognitionRequest> requests, CancellationToken ct)
    {
        var results = (await first.RecognizeAsync(alignedGray, requests, ct)).ToDictionary(r => (r.FieldKey, r.RowIndex));
        var unsure = requests.Where(q => !results.TryGetValue((q.FieldKey, q.RowIndex), out var r) || r.Text is null || r.Confidence < escalateBelow).ToList();
        if (unsure.Count == 0) return results.Values.ToList();

        try
        {
            foreach (var r in await second.RecognizeAsync(alignedGray, unsure, ct))
            {
                results.TryGetValue((r.FieldKey, r.RowIndex), out var current);
                if (r.Text is not null && (current?.Text is null || r.Confidence >= current.Confidence))
                    results[(r.FieldKey, r.RowIndex)] = r;
            }
            log.LogDebug("{Second} re-read {Count} uncertain box(es)", second.Name, unsure.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Escalation is optional: if the cloud / GPU service is down, keep the local readings (review fixes the rest).
            log.LogWarning(ex, "{Second} unavailable - using {First} results only", second.Name, first.Name);
        }
        return results.Values.ToList();
    }
}
