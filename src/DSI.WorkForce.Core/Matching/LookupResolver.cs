using System.Globalization;
using DSI.WorkForce.Core.Model;
using DSI.WorkForce.Core.Templates;

namespace DSI.WorkForce.Core.Matching;

/// <summary>
/// Snaps recognised text onto master data and fills the promoted header columns.
/// e.g. "29x2-10 SDG 94 F/B" -> item "29 2.10 SRI 94 Full Black" (ItemCode + standard rate come with it).
/// </summary>
public static class LookupResolver
{
    public const double ItemAutoAcceptScore = 0.85;
    public const double ItemMinScore = 0.60;

    public static void Apply(WorksheetExtraction ws, FormTemplateDefinition template, MasterDataSnapshot master)
    {
        foreach (var field in template.Fields.Where(f => f.Lookup is not null))
        {
            var v = ws.Get(field.Key);
            if (v is null || v.ValueText is null) continue;
            switch (field.Lookup)
            {
                case "Employee": ResolveEmployee(ws, template, v, master); break;
                case "Machine": ResolveMachine(ws, v, master); break;
                case "Item": ResolveItem(ws, v, master); break;
            }
        }

        ApplyUnitsPerTick(ws, template, master);
        Promote(ws, template);
    }

    private static void ResolveEmployee(WorksheetExtraction ws, FormTemplateDefinition t, ExtractedValue v, MasterDataSnapshot master)
    {
        if (master.Employees.Count == 0) return; // master not loaded yet -> skip check
        var epf = v.ValueText!.Trim();
        if (!master.Employees.TryGetValue(epf, out var emp))
        {
            v.Confidence = Math.Min(v.Confidence, 0.50m);
            ws.Issues.Add(new(v.FieldKey, 0, IssueSeverity.Error, "EPF_NOT_FOUND", $"EPF No '{epf}' is not in the employee master."));
            return;
        }

        // Cross-check: the handwritten operator name should resemble the master name.
        var nameField = t.Fields.FirstOrDefault(f => f.Key == "OPERATOR_NAME");
        var name = nameField is null ? null : ws.Get(nameField.Key);
        if (name?.ValueText is { Length: > 0 } written)
        {
            var sim = FuzzyMatcher.Similarity(written.ToUpperInvariant(), emp.Name.ToUpperInvariant());
            if (sim >= 0.5) { v.Confidence = Math.Max(v.Confidence, 0.97m); name.ValueText = emp.Name; name.Confidence = Math.Max(name.Confidence, 0.95m); name.Source = ValueSource.Lookup; }
            else ws.Issues.Add(new(v.FieldKey, 0, IssueSeverity.Warning, "EPF_NAME_MISMATCH", $"EPF {epf} belongs to '{emp.Name}' but the sheet says '{written}'."));
        }
    }

    private static void ResolveMachine(WorksheetExtraction ws, ExtractedValue v, MasterDataSnapshot master)
    {
        if (master.Machines.Count == 0) return;
        var code = FuzzyMatcher.CanonicalCode(v.ValueText!);
        if (master.Machines.TryGetValue(code, out var m)) { v.ValueText = m.ResourceNo; return; }

        var (best, score) = FuzzyMatcher.Best(code, master.Machines.Values, x => FuzzyMatcher.CanonicalCode(x.ResourceNo));
        if (best is not null && score >= 0.75)
        {
            v.ValueText = best.ResourceNo;
            v.Source = ValueSource.Lookup;
            v.Confidence = Math.Min(v.Confidence, (decimal)Math.Round(score, 2));
        }
        else
        {
            v.Confidence = Math.Min(v.Confidence, 0.40m);
            ws.Issues.Add(new(v.FieldKey, 0, IssueSeverity.Error, "MACHINE_NOT_FOUND", $"Press / resource '{v.ValueText}' is not in the machine master."));
        }
    }

    private static void ResolveItem(WorksheetExtraction ws, ExtractedValue v, MasterDataSnapshot master)
    {
        if (master.Items.Count == 0) return;
        var canon = FuzzyMatcher.CanonicalItem(v.ValueText!);

        if (master.ItemAliases.TryGetValue(canon, out var aliasCode) && master.Items.FirstOrDefault(i => i.ItemCode == aliasCode) is { } aliased)
        {
            SetItem(ws, v, aliased, Math.Max(v.Confidence, 0.95m));
            return;
        }

        var (best, score) = FuzzyMatcher.Best(canon, master.Items, i => FuzzyMatcher.CanonicalItem(i.Description));
        if (best is null || score < ItemMinScore)
        {
            v.Confidence = Math.Min(v.Confidence, 0.30m);
            ws.Issues.Add(new(v.FieldKey, 0, IssueSeverity.Error, "ITEM_NOT_FOUND", $"Tyre / item '{v.ValueText}' could not be matched to the item master."));
            return;
        }

        SetItem(ws, v, best, Math.Min(Math.Max(v.Confidence, 0.5m), (decimal)Math.Round(score, 2)));
        if (score < ItemAutoAcceptScore)
            ws.Issues.Add(new(v.FieldKey, 0, IssueSeverity.Warning, "ITEM_FUZZY_MATCH",
                $"'{v.RawText}' matched to '{best.Description}' ({score:P0}). Please confirm."));
    }

    private static void SetItem(WorksheetExtraction ws, ExtractedValue v, ItemMaster item, decimal confidence)
    {
        v.ValueText = item.Description;
        v.Source = ValueSource.Lookup;
        v.Confidence = confidence;
        ws.ItemCode = item.ItemCode;
    }

    /// <summary>Tally rows store tick count in RawText; value = ticks x units-per-tick (machine master overrides template).</summary>
    private static void ApplyUnitsPerTick(WorksheetExtraction ws, FormTemplateDefinition t, MasterDataSnapshot master)
    {
        var resourceField = t.Fields.FirstOrDefault(f => f.PromoteTo == "ResourceNo");
        decimal? machineUnits = null;
        if (resourceField is not null && ws.Get(resourceField.Key)?.ValueText is { } res
            && master.Machines.TryGetValue(FuzzyMatcher.CanonicalCode(res), out var m))
            machineUnits = m.UnitsPerTick;

        foreach (var f in t.Fields.Where(f => f.Extractor == ExtractorKind.OmrTally))
        {
            var perTick = machineUnits ?? f.UnitsPerTick;
            foreach (var row in ws.GetRows(f.Key))
                if (int.TryParse(row.RawText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                {
                    row.ValueNumber = ticks * perTick;
                    row.ValueText = row.ValueNumber.Value.ToString(CultureInfo.InvariantCulture);
                }
        }
    }

    private static void Promote(WorksheetExtraction ws, FormTemplateDefinition t)
    {
        foreach (var f in t.Fields.Where(f => f.PromoteTo is not null))
        {
            var v = ws.Get(f.Key);
            if (v is null) continue;
            switch (f.PromoteTo)
            {
                case "WorkDate": ws.WorkDate = v.ValueDate; break;
                case "Shift": ws.Shift = v.ValueText; break;
                case "ResourceNo": ws.ResourceNo = v.ValueText; break;
                case "EpfNo": ws.EpfNo = v.ValueText; break;
                case "ItemCode": ws.ItemCode ??= v.ValueText; break;
            }
        }
    }
}
