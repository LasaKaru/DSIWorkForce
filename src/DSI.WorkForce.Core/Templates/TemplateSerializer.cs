using System.Text.Json;
using System.Text.Json.Serialization;
using DSI.WorkForce.Core.Model;

namespace DSI.WorkForce.Core.Templates;

public static class TemplateSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static FormTemplateDefinition Deserialize(string json)
    {
        var t = JsonSerializer.Deserialize<FormTemplateDefinition>(json, Options)
                ?? throw new InvalidDataException("Template JSON is empty.");
        var errors = Validate(t);
        if (errors.Count > 0)
            throw new InvalidDataException($"Template '{t.TemplateCode}' is invalid:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", errors));
        return t;
    }

    public static string Serialize(FormTemplateDefinition t) => JsonSerializer.Serialize(t, Options);

    public static IReadOnlyList<string> Validate(FormTemplateDefinition t)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(t.TemplateCode)) errors.Add("templateCode is required.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in t.Fields)
        {
            if (string.IsNullOrWhiteSpace(f.Key) || !System.Text.RegularExpressions.Regex.IsMatch(f.Key, "^[A-Z][A-Z0-9_]{1,63}$"))
                errors.Add($"Field key '{f.Key}' must be UPPER_SNAKE_CASE (A-Z, 0-9, _).");
            else if (!keys.Add(f.Key)) errors.Add($"Duplicate field key '{f.Key}'.");

            var z = f.Zone;
            if (z.W <= 0 || z.H <= 0 || z.X < 0 || z.Y < 0 || z.X + z.W > 1.0001 || z.Y + z.H > 1.0001)
                errors.Add($"Field '{f.Key}': zone must be inside the page (fractions 0..1).");
            if (f.Rows < 1 || f.Rows > 50) errors.Add($"Field '{f.Key}': rows must be 1..50.");
            if (f.Extractor == ExtractorKind.OmrTally && (f.Columns < 1 || f.Type != FieldDataType.Integer))
                errors.Add($"Field '{f.Key}': OmrTally needs columns >= 1 and type Integer.");
            if (f.Rows * Math.Max(1, f.Columns) > 255) errors.Add($"Field '{f.Key}': rows x columns must be <= 255.");
            if (f.Extractor == ExtractorKind.Computed)
            {
                var src = f.SumOf is null ? null : t.Find(f.SumOf);
                if (src is null || !src.IsGrid) errors.Add($"Field '{f.Key}': Computed needs sumOf = a grid field (columns > 1).");
                else if (src.Rows != f.Rows) errors.Add($"Field '{f.Key}': rows must equal rows of '{src.Key}'.");
                if (f.Type is not (FieldDataType.Integer or FieldDataType.Decimal)) errors.Add($"Field '{f.Key}': Computed must be Integer or Decimal.");
            }
            if (f.Extractor == ExtractorKind.Omr && f.Type != FieldDataType.Checkbox)
                errors.Add($"Field '{f.Key}': Omr extractor needs type Checkbox.");
            if (f.PromoteTo is not null && !WorksheetHeaderColumns.Promotable.Contains(f.PromoteTo))
                errors.Add($"Field '{f.Key}': promoteTo must be one of {string.Join(", ", WorksheetHeaderColumns.Promotable)}.");
        }
        foreach (var r in t.Rules)
        {
            if (r.Field is not null && t.Find(r.Field) is null) errors.Add($"Rule {r.Type}: unknown field '{r.Field}'.");
            if (r.TotalField is not null && t.Find(r.TotalField) is null) errors.Add($"Rule {r.Type}: unknown field '{r.TotalField}'.");
        }
        return errors;
    }
}

/// <summary>Indexed columns on dsi.Worksheet that a field may be copied into for fast filtering.</summary>
public static class WorksheetHeaderColumns
{
    public static readonly IReadOnlySet<string> Promotable =
        new HashSet<string>(StringComparer.Ordinal) { "WorkDate", "Shift", "ResourceNo", "EpfNo", "ItemCode" };
}
