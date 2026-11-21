using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using DSI.WorkForce.Core.Model;
using DSI.WorkForce.Core.Normalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Local;

namespace DSI.WorkForce.Extraction.Recognition.Local;

/// <summary>
/// Offline handwriting recognizer: PaddleOCR PP-OCRv5 text-line model running on the CPU inside the Worker.
/// No cloud, no GPU, no per-page cost. Each box is read from several cleaned image variants and the best reading
/// that is valid for the field type wins (a number box must parse as a number, a date box as a date ...).
/// </summary>
public sealed class PaddleLocalRecognizer : IHandwritingRecognizer, IWarmUp, IDisposable
{
    private readonly RecognizationModel _model;
    private readonly LocalOcrOptions _o;
    private readonly ILogger<PaddleLocalRecognizer> _log;
    // PaddleOcrRecognizer is not thread-safe: keep a small pool, one instance per concurrently processed page.
    private readonly ConcurrentBag<PaddleOcrRecognizer> _pool = new();

    public string Name => "Local";

    public PaddleLocalRecognizer(IOptions<ExtractionOptions> options, ILogger<PaddleLocalRecognizer> log)
    {
        _o = options.Value.Local;
        _log = log;
        _model = _o.Model.Equals("EnglishV4", StringComparison.OrdinalIgnoreCase)
            ? LocalFullModels.EnglishV4.RecognizationModel
            : LocalFullModels.ChineseV5.RecognizationModel;   // PP-OCRv5: multilingual incl. digits/Latin, strongest on handwriting
    }

    public Task<IReadOnlyList<RecognitionResult>> RecognizeAsync(Mat alignedGray, IReadOnlyList<RecognitionRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0) return Task.FromResult<IReadOnlyList<RecognitionResult>>([]);
        var engine = Rent();
        try
        {
            // Pass 1 reads every box in one batched model call; passes 2-3 (other image variants) only re-read the
            // boxes that are still invalid for their field type or uncertain.
            var best = new Candidate?[requests.Count];
            var all = Enumerable.Range(0, requests.Count).Select(_ => new List<Candidate>()).ToArray();
            var variants = Math.Clamp(_o.Variants, 1, 3);
            for (var v = 0; v < variants; v++)
            {
                ct.ThrowIfCancellationRequested();
                var todo = Enumerable.Range(0, requests.Count)
                    .Where(i => best[i] is not { Valid: true } b || b.Confidence < GoodEnough).ToList();
                if (todo.Count == 0) break;

                var images = todo.Select(i => HandwritingCropper.Prepare(alignedGray, requests[i].Zone, v)).ToArray();
                try
                {
                    var read = RunBatched(engine, images);
                    for (var k = 0; k < todo.Count; k++)
                    {
                        var req = requests[todo[k]];
                        var c = Score(req, read[k].Text, read[k].Score);
                        _log.LogDebug("{Field}[{Row}] variant {Variant}: '{Text}' ({Score:0.00}) valid={Valid}", req.FieldKey, req.RowIndex, v, read[k].Text, read[k].Score, c.Valid);
                        all[todo[k]].Add(c);
                        if (best[todo[k]] is not { } prev || c.Rank > prev.Rank) best[todo[k]] = c;
                    }
                }
                finally { foreach (var img in images) img.Dispose(); }
            }
            for (var i = 0; i < requests.Count; i++)
                if (requests[i].Type is FieldDataType.Integer or FieldDataType.Decimal) best[i] = ResolveNumeric(all[i], best[i]);
            IReadOnlyList<RecognitionResult> results = requests
                .Select((r, i) => new RecognitionResult(r.FieldKey, r.RowIndex, best[i]?.Text, best[i]?.Confidence ?? 0m)).ToList();
            return Task.FromResult(results);
        }
        finally { _pool.Add(engine); }
    }

    /// <summary>
    /// Batched inference pads every image to the widest one in its batch, which hurts short boxes ("402" read as "2").
    /// So images are sorted by aspect ratio and batched with similar shapes only; results are returned in input order.
    /// </summary>
    private static PaddleOcrRecognizerResult[] RunBatched(PaddleOcrRecognizer engine, Mat[] images)
    {
        var order = Enumerable.Range(0, images.Length).OrderBy(i => images[i].Width / (double)images[i].Height).ToArray();
        var results = new PaddleOcrRecognizerResult[images.Length];
        foreach (var chunk in order.Chunk(8))
        {
            var ratioSpread = images[chunk[^1]].Width / (double)images[chunk[^1]].Height / (images[chunk[0]].Width / (double)images[chunk[0]].Height);
            if (ratioSpread > 1.6)
            {
                foreach (var i in chunk) results[i] = engine.Run(images[i]);   // shapes too different: one by one
                continue;
            }
            var read = engine.Run(chunk.Select(i => images[i]).ToArray(), chunk.Length);
            for (var k = 0; k < chunk.Length; k++) results[chunk[k]] = read[k];
        }
        return results;
    }

    /// <summary>
    /// Image variants that read different numbers mean the box is uncertain. Handwriting OCR far more often drops a
    /// character than invents one (".02" for "402"), so the reading with more digits is preferred - and its confidence
    /// is capped so a person checks it.
    /// </summary>
    private static Candidate? ResolveNumeric(List<Candidate> candidates, Candidate? best)
    {
        var valid = candidates.Where(c => c.Valid && c.Text is not null)
            .Select(c => (c, Value: ValueNormalizer.ParseNumber(c.Text!, integerOnly: false).Value))
            .Where(x => x.Value is not null).ToList();
        if (valid.Select(x => x.Value).Distinct().Count() <= 1) return best;
        var pick = valid.Where(x => x.c.Confidence >= 0.5m).OrderByDescending(x => x.c.Text!.Count(char.IsDigit) + x.c.Text!.Count(ch => "OoDQIlSZBGHKbgqT".Contains(ch)))
            .ThenByDescending(x => x.c.Confidence).Select(x => x.c).FirstOrDefault() ?? best;
        return pick is null ? null : pick with { Confidence = Math.Min(pick.Confidence, 0.7m) };
    }

    /// <summary>A valid reading at or above this score is not re-read with other image variants.</summary>
    private const decimal GoodEnough = 0.85m;

    /// <summary>Loads the model into <paramref name="instances"/> engines up front, so the first pages are not slowed down.</summary>
    public void WarmUp(int instances)
    {
        var loaded = new List<PaddleOcrRecognizer>();
        for (var i = _pool.Count; i < instances; i++) loaded.Add(Rent());
        foreach (var e in loaded) _pool.Add(e);
    }

    private PaddleOcrRecognizer Rent()
    {
        if (_pool.TryTake(out var r)) return r;
        _log.LogInformation("Loading PaddleOCR model {Model} ({Threads} CPU threads)", _o.Model, _o.CpuThreads);
        return new PaddleOcrRecognizer(_model, PaddleDevice.Mkldnn(cpuMathThreadCount: _o.CpuThreads));
    }

    private sealed record Candidate(string? Text, decimal Confidence, bool Valid)
    {
        // Valid readings always beat invalid ones; then the higher model score wins.
        public decimal Rank => (Valid ? 10m : 0m) + Confidence;
    }

    private static Candidate Score(RecognitionRequest req, string? raw, float score)
    {
        var text = Clean(raw, req.Type);
        if (!float.IsFinite(score)) score = 0f;   // Paddle reports NaN for an empty image
        var conf = (decimal)Math.Round(Math.Clamp(score, 0f, 1f), 2);
        if (string.IsNullOrWhiteSpace(text)) return new(null, 0m, false);

        var valid = req.Type switch
        {
            FieldDataType.Integer => ValueNormalizer.ParseNumber(text, integerOnly: true).Value is not null,
            FieldDataType.Decimal => ValueNormalizer.ParseNumber(text, integerOnly: false).Value is not null,
            FieldDataType.Date => ValueNormalizer.ParseDate(text, null, DateOnly.FromDateTime(DateTime.Today)).Date is not null,
            FieldDataType.Time => ValueNormalizer.NormalizeTime(text) is not null || text.Any(char.IsDigit),
            _ => text.Any(char.IsLetterOrDigit),
        };
        return new(text, valid ? conf : Math.Min(conf, 0.5m), valid);
    }

    /// <summary>The multilingual model sometimes returns CJK look-alikes for Latin strokes; map or drop them.</summary>
    private static string Clean(string? raw, FieldDataType type)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Normalize(NormalizationForm.FormKC))
        {
            var c = ch switch
            {
                '一' or '—' or '–' or '－' => '-',
                '。' or '．' => '.',
                '／' => '/',
                '：' => ':',
                _ => ch,
            };
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            var isCjk = c >= 0x2E80 && c <= 0x9FFF || c >= 0xF900 && c <= 0xFAFF;
            if (isCjk || cat is UnicodeCategory.Control or UnicodeCategory.OtherNotAssigned) continue;
            sb.Append(c);
        }
        var s = sb.ToString().Trim();
        return type is FieldDataType.Integer or FieldDataType.Decimal or FieldDataType.Date or FieldDataType.Time ? s.Replace(" ", "") : s;
    }

    public void Dispose()
    {
        while (_pool.TryTake(out var r)) r.Dispose();
    }
}
