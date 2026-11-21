using System.Globalization;
using DSI.WorkForce.Core.Matching;
using DSI.WorkForce.Core.Model;
using DSI.WorkForce.Core.Normalization;
using DSI.WorkForce.Core.Omr;
using DSI.WorkForce.Core.Templates;
using DSI.WorkForce.Core.Validation;
using DSI.WorkForce.Extraction.Imaging;
using DSI.WorkForce.Extraction.Recognition;
using DSI.WorkForce.Extraction.Recognition.Digits;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCvSharp;

namespace DSI.WorkForce.Extraction;

/// <summary>
/// Page pipeline: align -> OMR tick boxes &amp; tally grid -> handwriting (only non-blank boxes)
/// -> normalize -> master-data lookups -> business rules -> AutoApproved / NeedsReview.
/// </summary>
public sealed class FormExtractor(IHandwritingRecognizer recognizer, IOptions<ExtractionOptions> options, ILogger<FormExtractor> log,
    DigitCellReader? digits = null)
{
    /// <summary>A digit-model reading at or above this is used directly; below it the text recognizer is asked too.</summary>
    private const decimal DigitAcceptConfidence = 0.90m;

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
        var digitGuesses = new Dictionary<(string, int), DigitReading>();

        // Tables: use the real printed row / column lines instead of an even split of the template zone.
        var layouts = template.Fields
            .Where(f => f.Extractor != ExtractorKind.Computed && (f.IsGrid || f.IsMultiRow))
            .ToDictionary(f => f.Key, f => GridSnapper.Snap(alignment.Aligned, f.Zone, f.Rows, f.IsGrid ? f.Columns : 1));
        Zone ZoneFor(FieldDefinition f, int row)
        {
            if (!layouts.TryGetValue(f.Key, out var l)) return f.ZoneOf(row);
            if (f.IsGrid) { var (r, c) = f.CellOf(row); return l.Cell(r, c); }
            return l.Cell(row == 0 ? 1 : row, 1);
        }

        foreach (var f in template.Fields.Where(f => f.Extractor != ExtractorKind.Computed))
        {
            // row = 0 (single box), 1..Rows (table column) or 1..Rows*Columns (grid cell)
            foreach (var row in f.RowIndexes())
            {
                var zone = ZoneFor(f, row);
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
                        // Number cells (hourly grid, single digits): dedicated handwritten-digit model first. It is built
                        // for isolated digits, so wider multi-digit boxes (EPF, target, total) stay with the text OCR.
                        if (digits is not null && _opt.UseDigitModel && f.IsGrid && f.Type == FieldDataType.Integer
                            && digits.Read(alignment.Aligned, zone) is { } d)
                        {
                            if (d.Text is null || d.Confidence >= DigitAcceptConfidence)
                            {
                                ws.Values.Add(ValueNormalizer.Normalize(f, row, d.Text, d.Confidence, ValueSource.Ocr, referenceDate));
                                continue;
                            }
                            digitGuesses[(f.Key, row)] = d;   // unsure: let the text recognizer give a second opinion
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
                var (text, conf) = (r?.Text, r?.Confidence ?? 0m);
                if (digitGuesses.TryGetValue((req.FieldKey, req.RowIndex), out var guess))
                    (text, conf) = CombineDigitOpinions(guess, text, conf);
                ws.Values.Add(ValueNormalizer.Normalize(field, req.RowIndex, text, conf, ValueSource.Ocr, referenceDate));
            }
        }

        ComputedFields.Apply(ws, template);   // e.g. hourly units = sum of the 12 numbers in that hour row
        LookupResolver.Apply(ws, template, master);
        WorksheetValidator.Validate(ws, template);

        Cv2.ImEncode(".jpg", alignment.Aligned, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, _opt.AlignedImageJpegQuality));
        ws.AlignedImageJpeg = jpeg;
        return ws;
    }

    /// <summary>Two independent readings of a number cell: agreement = trusted, disagreement = keep the stronger, review it.</summary>
    private static (string? Text, decimal Confidence) CombineDigitOpinions(DigitReading digit, string? text, decimal textConfidence)
    {
        var a = ValueNormalizer.ParseNumber(digit.Text ?? "", integerOnly: true).Value;
        var b = text is null ? null : ValueNormalizer.ParseNumber(text, integerOnly: true).Value;
        if (a is not null && a == b) return (digit.Text, Math.Max(0.95m, Math.Max(digit.Confidence, textConfidence)));
        if (b is null) return (digit.Text, Math.Min(digit.Confidence, 0.6m));
        return digit.Confidence >= textConfidence ? (digit.Text, Math.Min(digit.Confidence, 0.6m)) : (text, Math.Min(textConfidence, 0.6m));
    }

    private static string Describe(FieldDefinition f, int row)
    {
        if (f.IsGrid) { var (r, c) = f.CellOf(row); return $"{f.Label} - hour {r}, cell {c}"; }
        return f.IsMultiRow ? $"{f.Label} #{row}" : f.Label;
    }
}
