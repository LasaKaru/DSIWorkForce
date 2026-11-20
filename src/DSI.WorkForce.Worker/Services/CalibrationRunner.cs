using System.Text.Json;
using DSI.WorkForce.Core.Matching;
using DSI.WorkForce.Core.Model;
using DSI.WorkForce.Core.Templates;
using DSI.WorkForce.Extraction;
using DSI.WorkForce.Extraction.Imaging;
using OpenCvSharp;

namespace DSI.WorkForce.Worker.Services;

/// <summary>
/// "DSI.WorkForce.Worker --calibrate scan.pdf [--template templates/forms/PC-011-A.v1.json] [--out folder]"
/// Runs extraction WITHOUT a database and writes an overlay image showing every zone, so you can adjust
/// the zone coordinates in the template JSON until the boxes line up with the printed form.
/// </summary>
public static class CalibrationRunner
{
    public static async Task<int> RunAsync(string[] args, FormExtractor extractor)
    {
        string Arg(string name, string fallback) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        var input = Arg("--calibrate", "");
        var templatePath = Arg("--template", Path.Combine(AppContext.BaseDirectory, "templates", "forms", "PC-011-A.v1.json"));
        var outDir = Arg("--out", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".", "calibration"));
        Directory.CreateDirectory(outDir);

        var template = TemplateSerializer.Deserialize(await File.ReadAllTextAsync(templatePath));
        var bytes = await File.ReadAllBytesAsync(input);
        var isPdf = input.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        var pages = isPdf ? PdfPageRenderer.GetPageCount(bytes) : 1;

        for (var p = 1; p <= pages; p++)
        {
            using var page = isPdf ? PdfPageRenderer.RenderPage(bytes, p - 1, template.Alignment.RenderDpi) : PdfPageRenderer.LoadImage(bytes);
            var ws = await extractor.ExtractAsync(page, p, template, MasterDataSnapshot.Empty, DateOnly.FromDateTime(DateTime.Today), CancellationToken.None);

            using var aligned = Cv2.ImDecode(ws.AlignedImageJpeg!, ImreadModes.Color);
            foreach (var f in template.Fields)
                for (var r = 1; r <= f.Rows; r++)
                {
                    var v = ws.Get(f.Key, f.IsMultiRow ? r : 0);
                    var color = v is null ? Scalar.Gray
                        : v.Confidence < template.AutoApproveConfidence ? Scalar.Red
                        : v.IsEmpty || v.ValueBool == false ? Scalar.Orange : Scalar.LimeGreen;
                    var z = f.Zone.Row(r, f.Rows);
                    var rect = new Rect((int)(z.X * aligned.Width), (int)(z.Y * aligned.Height), (int)(z.W * aligned.Width), (int)(z.H * aligned.Height));
                    Cv2.Rectangle(aligned, rect, color, 2);
                    if (f.Extractor == ExtractorKind.OmrTally)
                        for (var c = 1; c < f.Columns; c++)
                        {
                            var x = rect.X + rect.Width * c / f.Columns;
                            Cv2.Line(aligned, new Point(x, rect.Y), new Point(x, rect.Bottom), Scalar.SkyBlue, 1);
                        }
                    if (r == 1) Cv2.PutText(aligned, f.Key, new Point(rect.X + 2, rect.Y + 14), HersheyFonts.HersheyPlain, 0.9, Scalar.Blue, 1);
                    if (v?.ValueText is { } text && f.Extractor is ExtractorKind.OmrTally)
                        Cv2.PutText(aligned, $"{v.RawText} ticks = {text}", new Point(rect.Right + 4, rect.Y + rect.Height - 6), HersheyFonts.HersheyPlain, 1.0, Scalar.Red, 1);
                }

            var stem = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(input)}_p{p}");
            Cv2.ImWrite(stem + "_overlay.png", aligned);
            await File.WriteAllTextAsync(stem + "_values.json", JsonSerializer.Serialize(new
            {
                ws.AlignmentOk, Status = ws.Status.ToString(), ws.Issues,
                Values = ws.Values.Select(v => new { v.FieldKey, v.RowIndex, v.RawText, v.ValueText, v.Confidence, Source = v.Source.ToString() }),
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Page {p}: alignment {(ws.AlignmentOk ? "OK" : "FAILED")}, status {ws.Status}, {ws.Issues.Count} issue(s)");
            Console.WriteLine($"  -> {stem}_overlay.png");
            Console.WriteLine($"  -> {stem}_values.json");
        }
        return 0;
    }
}
