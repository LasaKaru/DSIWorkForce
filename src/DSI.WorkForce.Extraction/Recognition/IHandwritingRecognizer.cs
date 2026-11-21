using DSI.WorkForce.Core.Model;
using DSI.WorkForce.Core.Templates;
using OpenCvSharp;

namespace DSI.WorkForce.Extraction.Recognition;

public sealed record RecognitionRequest(string FieldKey, int RowIndex, Zone Zone, FieldDataType Type, string Label, string? Hint);

public sealed record RecognitionResult(string FieldKey, int RowIndex, string? Text, decimal Confidence);

/// <summary>
/// Reads handwriting inside the given zones of an aligned page. Implementations are swappable by configuration
/// (Extraction:Recognizer) so you can start with manual keying, then switch to Azure or a local GPU model.
/// </summary>
public interface IHandwritingRecognizer
{
    string Name { get; }
    Task<IReadOnlyList<RecognitionResult>> RecognizeAsync(Mat alignedGray, IReadOnlyList<RecognitionRequest> requests, CancellationToken ct);
}

/// <summary>Recognizers with a slow first call (model loading) can be prepared when the Worker starts.</summary>
public interface IWarmUp
{
    void WarmUp(int instances);
}

/// <summary>No AI: handwriting boxes go to the review screen with the image snippet next to an input box.</summary>
public sealed class ManualKeyingRecognizer : IHandwritingRecognizer
{
    public string Name => "None";

    public Task<IReadOnlyList<RecognitionResult>> RecognizeAsync(Mat alignedGray, IReadOnlyList<RecognitionRequest> requests, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RecognitionResult>>(requests.Select(r => new RecognitionResult(r.FieldKey, r.RowIndex, null, 0m)).ToList());
}
