using System.Globalization;
using DSI.WorkForce.Core.Matching;
using DSI.WorkForce.Core.Model;
using DSI.WorkForce.Core.Normalization;
using DSI.WorkForce.Core.Omr;
using DSI.WorkForce.Core.Templates;
using DSI.WorkForce.Core.Validation;
using DSI.WorkForce.Extraction.Imaging;
using DSI.WorkForce.Extraction.Recognition;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCvSharp;

namespace DSI.WorkForce.Extraction;

/// <summary>
/// Page pipeline: align -> OMR tick boxes &amp; tally grid -> handwriting (only non-blank boxes)
/// -> normalize -> master-data lookups -> business rules -> AutoApproved / NeedsReview.
/// </summary>
public sealed class FormExtractor(IHandwritingRecognizer recognizer, IOptions<ExtractionOptions> options, ILogger<FormExtractor> log)
{
    private readonly ExtractionOptions _opt = options.Value;

    /// <summary>Prepares the handwriting engine (model loading) before the first page arrives.</summary>
    public void WarmUp(int instances) => (recognizer as IWarmUp)?.WarmUp(instances);

    public async Task<WorksheetExtraction> ExtractAsync(Mat pageGray, int pageNo, FormTemplateDefinition template,
        MasterDataSnapshot master, DateOnly referenceDate, CancellationToken ct)
    {
        var ws = new WorksheetExtraction { TemplateCode = template.TemplateCode, TemplateVersion = template.Version, PageNo = pageNo };

        using var alignment = FormAligner.Align(pageGray, template.Alignment);
        ws.AlignmentOk = alignment.Success;
        if (!alignment.Success) log.LogWarning("Page {Page}: alignment failed: {Reason}", pageNo, alignment.Message);

        using var ink = new InkMeter(alignment.Aligned, template.Omr);
        var omr = template.Omr;
        var pending = new List<(FieldDefinition Field, RecognitionRequest Request)>();

        foreach (var f in template.Fields.Where(f => f.Extractor != ExtractorKind.Computed))
        {
            // row = 0 (single box), 1..Rows (table column) or 1..Rows*Columns (grid cell)
            foreach (var row in f.RowIndexes())
            {
                var zone = f.ZoneOf(row);
                switch (f.Extractor)
                {
                    case ExtractorKind.Omr:
                    {
                        var ratio = ink.InkRatio(zone, omr.CellInset);
                        var (state, conf) = OmrClassifier.Classify(ratio, f.MarkThreshold ?? omr.MarkThreshold, omr.UncertainBand);
                        var marked = state == MarkState.Marked || state == MarkState.Uncertain && ratio >= (f.MarkThreshold ?? omr.MarkThreshold);
                        ws.Values.Add(new ExtractedValue
                        {
                            FieldKey = f.Key, RowIndex = row, RawText = ratio.ToString("0.000", CultureInfo.InvariantCulture),
                            ValueBool = marked, ValueText = marked ? "1" : "0", Confidence = conf, Source = ValueSource.Omr,
                        });
                        break;
                    }
                    case ExtractorKind.OmrTally:
                    {
                        var ratios = Enumerable.Range(1, f.Columns).Select(c => ink.InkRatio(zone.Col(c, f.Columns), omr.CellInset)).ToList();
                        var (ticks, conf, _) = OmrClassifier.CountTally(ratios, f.MarkThreshold ?? omr.MarkThreshold, omr.UncertainBand);
                        var units = ticks * f.UnitsPerTick;   // machine master may override in LookupResolver
                        ws.Values.Add(new ExtractedValue
                        {
                            FieldKey = f.Key, RowIndex = row, RawText = ticks.ToString(CultureInfo.InvariantCulture),
                            ValueNumber = units, ValueText = units.ToString(CultureInfo.InvariantCulture), Confidence = conf, Source = ValueSource.Omr,
                        });
                        break;
                    }
                    default:
                    {
                        // Blank box? Then we already know the answer: skip the (slow / paid) recognizer.
                        var ratio = ink.InkRatioIgnoringLines(zone, 0.06);
                        if (ratio < _opt.BlankInkThreshold)
                        {
                            ws.Values.Add(new ExtractedValue { FieldKey = f.Key, RowIndex = row, Confidence = 0.99m, Source = ValueSource.Omr });
                            continue;
                        }
                        if (f.Extractor == ExtractorKind.Manual)
                        {
                            ws.Values.Add(new ExtractedValue { FieldKey = f.Key, RowIndex = row, RawText = "[handwriting]", Confidence = 0m, Source = ValueSource.Manual });
                            continue;
                        }
                        pending.Add((f, new RecognitionRequest(f.Key, row, zone, f.Type, Describe(f, row), f.Hint)));
                        break;
                    }
                }
            }
        }

        if (pending.Count > 0)
        {
            var results = await recognizer.RecognizeAsync(alignment.Aligned, pending.Select(p => p.Request).ToList(), ct);
            var byKey = results.ToDictionary(r => (r.FieldKey, r.RowIndex));
            foreach (var (field, req) in pending)
            {
                byKey.TryGetValue((req.FieldKey, req.RowIndex), out var r);
                ws.Values.Add(ValueNormalizer.Normalize(field, req.RowIndex, r?.Text, r?.Confidence ?? 0m, ValueSource.Ocr, referenceDate));
            }
        }

        ComputedFields.Apply(ws, template);   // e.g. hourly units = sum of the 12 numbers in that hour row
        LookupResolver.Apply(ws, template, master);
        WorksheetValidator.Validate(ws, template);

        Cv2.ImEncode(".jpg", alignment.Aligned, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, _opt.AlignedImageJpegQuality));
        ws.AlignedImageJpeg = jpeg;
        return ws;
    }

    private static string Describe(FieldDefinition f, int row)
    {
        if (f.IsGrid) { var (r, c) = f.CellOf(row); return $"{f.Label} - hour {r}, cell {c}"; }
        return f.IsMultiRow ? $"{f.Label} #{row}" : f.Label;
    }
}
