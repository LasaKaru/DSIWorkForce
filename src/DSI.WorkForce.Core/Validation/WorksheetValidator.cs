using DSI.WorkForce.Core.Model;
using DSI.WorkForce.Core.Templates;

namespace DSI.WorkForce.Core.Validation;

/// <summary>
/// Business-rule checks run after extraction. Decides AutoApproved vs NeedsReview so supervisors only
/// look at the sheets (and cells) that actually need a human.
/// </summary>
public static class WorksheetValidator
{
    public static void Validate(WorksheetExtraction ws, FormTemplateDefinition t)
    {
        if (!ws.AlignmentOk)
            ws.Issues.Add(new(null, 0, IssueSeverity.Error, "ALIGNMENT_FAILED", "Form border not found - page may be skewed, folded or a different form."));

        foreach (var f in t.Fields.Where(f => f.Required))
        {
            var rows = f.IsMultiRow || f.IsGrid ? ws.GetRows(f.Key).ToList() : ws.Get(f.Key) is { } single ? [single] : [];
            // Only "confidently blank" counts as missing; "could not read" is reported as NOT_READ below.
            if (rows.Count == 0 || rows.All(r => r.IsEmpty && r.Confidence >= 0.9m))
                ws.Issues.Add(new(f.Key, 0, IssueSeverity.Error, "REQUIRED", $"{f.Label} is required but the box is empty."));
        }

        foreach (var r in t.Rules) ApplyRule(ws, r, t);

        // Computed values (row sums) are not reported separately: the cells they come from already are.
        var computed = t.Fields.Where(f => f.Extractor == ExtractorKind.Computed).Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var v in ws.Values.Where(v => v.Confidence < t.AutoApproveConfidence && !computed.Contains(v.FieldKey)))
            if (!ws.Issues.Any(i => i.FieldKey == v.FieldKey && i.RowIndex == v.RowIndex))
                ws.Issues.Add(v.IsEmpty
                    ? new(v.FieldKey, v.RowIndex, IssueSeverity.Warning, "NOT_READ", "Handwriting could not be read automatically - please type the value.")
                    : new(v.FieldKey, v.RowIndex, IssueSeverity.Warning, "LOW_CONFIDENCE", $"Low confidence ({v.Confidence:P0}) - please check."));

        var blocking = ws.Issues.Any(i => i.Severity >= IssueSeverity.Warning);
        ws.Status = !blocking && ws.MinConfidence >= t.AutoApproveConfidence ? WorksheetStatus.AutoApproved : WorksheetStatus.NeedsReview;
    }

    private static void ApplyRule(WorksheetExtraction ws, RuleDefinition r, FormTemplateDefinition t)
    {
        switch (r.Type)
        {
            case "TallyMatchesTotal":
            case "SumMatchesTotal":
                if (r.Field is null || r.TotalField is null) return;
                var rows = ws.GetRows(r.Field).Where(x => x.ValueNumber is not null).ToList();
                var total = ws.Get(r.TotalField);
                if (rows.Count == 0 || total?.ValueNumber is null) return;
                var sum = rows.Sum(x => x.ValueNumber!.Value);
                if (Math.Abs(sum - total.ValueNumber.Value) <= r.Tolerance)
                {
                    // Two independent readings agree (hourly cells vs handwritten total): trust both,
                    // including the individual cells a row sum was computed from.
                    foreach (var x in rows) x.Confidence = Math.Max(x.Confidence, 0.98m);
                    total.Confidence = Math.Max(total.Confidence, 0.98m);
                    if (t.Find(r.Field)?.SumOf is { } cellsKey)
                        foreach (var c in ws.GetRows(cellsKey).Where(c => c.ValueNumber is not null))
                            c.Confidence = Math.Max(c.Confidence, 0.98m);
                }
                else
                {
                    ws.Issues.Add(new(r.TotalField, 0, r.Severity, "TOTAL_MISMATCH",
                        r.Message ?? $"Sum of {r.Field} = {sum} but {r.TotalField} = {total.ValueNumber}."));
                }
                break;
        }
    }
}
