using DSI.WorkForce.Core.Reporting;
using DSI.WorkForce.Core.Templates;
using System.Text.Json;

namespace DSI.WorkForce.Tests;

internal static class TestData
{
    public static string TemplatesDir => Path.Combine(AppContext.BaseDirectory, "templates");

    public static FormTemplateDefinition PressTemplate() =>
        TemplateSerializer.Deserialize(File.ReadAllText(Path.Combine(TemplatesDir, "forms", "PC-011-A.v1.json")));

    public static ReportDefinition DailyEfficiency() =>
        JsonSerializer.Deserialize<ReportDefinition>(File.ReadAllText(Path.Combine(TemplatesDir, "reports", "DailyEfficiency.json")), TemplateSerializer.Options)!;

    public static IReadOnlyDictionary<string, FieldInfo> FieldMap(FormTemplateDefinition t) =>
        t.Fields.Select((f, i) => new FieldInfo(i + 1, f.Key, f.Type)).ToDictionary(f => f.FieldKey, StringComparer.OrdinalIgnoreCase);
}
