using DSI.WorkForce.Core.Reporting;
using DSI.WorkForce.Core.Templates;
using System.Text.Json;

namespace DSI.WorkForce.Tests;

internal static class TestData
{
    public static string TemplatesDir => Path.Combine(AppContext.BaseDirectory, "templates");

    public static FormTemplateDefinition PressTemplate() =>
        TemplateSerializer.Deserialize(File.ReadAllText(Path.Combine(TemplatesDir, "forms", "PC-011-A.v1.json")));

    public static ReportDefinition DailyEfficiency() => Report("DailyEfficiency");

    public static ReportDefinition Report(string code) =>
        JsonSerializer.Deserialize<ReportDefinition>(File.ReadAllText(Path.Combine(TemplatesDir, "reports", code + ".json")), TemplateSerializer.Options)!;

    public static IEnumerable<string> ReportCodes() =>
        Directory.EnumerateFiles(Path.Combine(TemplatesDir, "reports"), "*.json").Select(Path.GetFileNameWithoutExtension)!;

    /// <summary>Same shape as TemplateRepository.GetFieldMapAsync returns from dsi.FieldDefinition.</summary>
    public static IReadOnlyDictionary<string, FieldInfo> FieldMap(FormTemplateDefinition t) =>
        t.Fields.Select((f, i) => new FieldInfo(i + 1, f.Key, f.Type, f.Rows, f.IsGrid ? f.Columns : 1))
            .ToDictionary(f => f.FieldKey, StringComparer.OrdinalIgnoreCase);
}
