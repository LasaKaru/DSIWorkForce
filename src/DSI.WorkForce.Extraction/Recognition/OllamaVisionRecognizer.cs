using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCvSharp;

namespace DSI.WorkForce.Extraction.Recognition;

/// <summary>
/// On-premise option: a local vision-language model served by Ollama (e.g. qwen2.5vl:7b on one RTX GPU).
/// All handwriting snippets of a sheet are stacked into ONE numbered "contact sheet" image and read in a
/// single call that must return strict JSON, e.g. {"1":"P-24","2":"1234"}.
/// </summary>
public sealed class OllamaVisionRecognizer(HttpClient http, IOptions<ExtractionOptions> options, ILogger<OllamaVisionRecognizer> log)
    : IHandwritingRecognizer
{
    private readonly OllamaOptions _o = options.Value.Ollama;
    public string Name => "Ollama";

    public async Task<IReadOnlyList<RecognitionResult>> RecognizeAsync(Mat alignedGray, IReadOnlyList<RecognitionRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0) return [];
        var jpeg = BuildContactSheet(alignedGray, requests);

        var prompt = new StringBuilder();
        prompt.AppendLine("The image shows numbered snippets cut from a handwritten tyre-factory production worksheet.");
        prompt.AppendLine("Read the handwriting in each snippet exactly. Return ONLY a JSON object mapping the snippet number to the text.");
        prompt.AppendLine("Use \"\" for an empty snippet. Do not guess missing digits. Snippets:");
        for (var i = 0; i < requests.Count; i++)
            prompt.AppendLine($"{i + 1}: {requests[i].Label} ({requests[i].Type}){(requests[i].Hint is { } h ? " - " + h : "")}");

        var body = new
        {
            model = _o.Model,
            stream = false,
            format = "json",
            options = new { temperature = 0 },
            messages = new[] { new { role = "user", content = prompt.ToString(), images = new[] { Convert.ToBase64String(jpeg) } } },
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_o.TimeoutSeconds));
        using var resp = await http.PostAsJsonAsync(_o.BaseUrl.TrimEnd('/') + "/api/chat", body, cts.Token);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(cts.Token), cancellationToken: cts.Token);
        var content = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "{}";

        Dictionary<string, JsonElement>? map;
        try { map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(content); }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "VLM returned non-JSON output: {Content}", content.Length > 300 ? content[..300] : content);
            map = null;
        }

        return requests.Select((r, i) =>
        {
            string? text = null;
            if (map is not null && map.TryGetValue((i + 1).ToString(), out var el))
                text = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
            return new RecognitionResult(r.FieldKey, r.RowIndex, string.IsNullOrWhiteSpace(text) ? null : text, text is null ? 0m : _o.AssumedConfidence);
        }).ToList();
    }

    private static byte[] BuildContactSheet(Mat page, IReadOnlyList<RecognitionRequest> requests)
    {
        // Tile the snippets so ~100 hourly cells still make a readable image of reasonable size.
        var tilesPerRow = requests.Count <= 12 ? 1 : requests.Count <= 40 ? 3 : 5;
        int tileW = tilesPerRow == 1 ? 970 : 380, tileH = 90, labelW = 70;
        var rowsOfTiles = (requests.Count + tilesPerRow - 1) / tilesPerRow;
        using var sheet = new Mat(tileH * rowsOfTiles, tileW * tilesPerRow, MatType.CV_8UC1, Scalar.White);
        for (var i = 0; i < requests.Count; i++)
        {
            int ox = i % tilesPerRow * tileW, oy = i / tilesPerRow * tileH;
            var z = requests[i].Zone;
            var rect = new Rect((int)(z.X * page.Width), (int)(z.Y * page.Height), (int)(z.W * page.Width), (int)(z.H * page.Height))
                .Intersect(new Rect(0, 0, page.Width, page.Height));
            if (rect.Width > 0 && rect.Height > 0)
            {
                using var crop = new Mat(page, rect);
                var scale = Math.Min((tileH - 6) / (double)rect.Height, (tileW - labelW - 6) / (double)rect.Width);
                using var resized = new Mat();
                Cv2.Resize(crop, resized, new Size(Math.Max(1, (int)(rect.Width * scale)), Math.Max(1, (int)(rect.Height * scale))));
                using var target = new Mat(sheet, new Rect(ox + labelW, oy + 3, resized.Width, resized.Height));
                resized.CopyTo(target);
            }
            Cv2.PutText(sheet, (i + 1).ToString(), new Point(ox + 6, oy + tileH / 2 + 12), HersheyFonts.HersheySimplex, 1.0, Scalar.Black, 2);
            Cv2.Rectangle(sheet, new Rect(ox, oy, tileW - 1, tileH - 1), Scalar.Gray, 1);
        }
        Cv2.ImEncode(".jpg", sheet, out var bytes, new ImageEncodingParam(ImwriteFlags.JpegQuality, 90));
        return bytes;
    }
}
