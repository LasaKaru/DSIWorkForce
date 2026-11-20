using System.IO;
using System.Text.Json;

namespace DSI.WorkForce.Desktop.Services;

public sealed class DesktopSettings
{
    public string ApiBaseUrl { get; set; } = "https://localhost:5001/";
    public string TemplateCode { get; set; } = "PC-011-A";
    public string? WatchFolder { get; set; }
    public bool AutoUploadWatchedFolder { get; set; }

    public static DesktopSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "desktopsettings.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new()
            : new();
    }
}
