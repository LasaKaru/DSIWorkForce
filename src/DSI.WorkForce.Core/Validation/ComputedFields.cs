using DSI.WorkForce.Core.Model;
using DSI.WorkForce.Core.Templates;

namespace DSI.WorkForce.Core.Validation;

/// <summary>
/// Fills Computed fields, e.g. HOURLY_UNITS row r = sum of the 12 handwritten numbers in hour row r of HOURLY_CELL.
/// Runs after extraction and again after every manual correction, so totals always match the cells.
/// </summary>
public static class ComputedFields
{
    public static void Apply(WorksheetExtraction ws, FormTemplateDefinition t)
    {
        foreach (var f in t.Fields.Where(f => f.Extractor == ExtractorKind.Computed && f.SumOf is not null))
        {
            var src = t.Find(f.SumOf!);
            if (src is null || !src.IsGrid) continue;
            ws.Values.RemoveAll(v => string.Equals(v.FieldKey, f.Key, StringComparison.OrdinalIgnoreCase));

            for (var r = 1; r <= f.Rows; r++)
            {
                var cells = Enumerable.Range(1, src.Columns).Select(c => ws.Get(src.Key, src.CellIndex(r, c))).Where(c => c is not null).ToList();
                var filled = cells.Where(c => !c!.IsEmpty).ToList();
                var unread = cells.Any(c => c!.IsEmpty && c.Confidence < 0.9m) || filled.Any(c => c!.ValueNumber is null);
                var value = new ExtractedValue { FieldKey = f.Key, RowIndex = r, Source = ValueSource.Computed };

                if (unread) value.Confidence = 0m;                         // some cell still needs a human
                else if (filled.Count == 0) value.Confidence = 0.99m;      // empty hour row
                else
                {
                    value.ValueNumber = filled.Sum(c => c!.ValueNumber!.Value);
                    value.ValueText = value.ValueNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    value.RawText = $"sum of {filled.Count} cell(s)";
                    value.Confidence = filled.Min(c => c!.Confidence);
                }
                ws.Values.Add(value);
            }
        }
    }
}
