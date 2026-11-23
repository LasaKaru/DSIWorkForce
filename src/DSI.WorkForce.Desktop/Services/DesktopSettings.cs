using System.IO;
using System.Text.Json;

namespace DSI.WorkForce.Desktop.Services;

public sealed class DesktopSettings
{
    public string ApiBaseUrl { get; set; } = "https://localhost:5001/";
    public string TemplateCode { get; set; } = "PC-011-A";
    public string? WatchFolder { get; set; }
    public bool AutoUploadWatchedFolder { get; set; }

    /// <summary>
    /// Bypass the HTTPS certificate check (development PCs with an untrusted localhost certificate, test servers).
    /// Leave false in production: use a trusted certificate or pin it with <see cref="TrustedCertificateThumbprint"/>.
    /// </summary>
    public bool AllowUntrustedCertificate { get; set; }

    /// <summary>Accept exactly this server certificate (SHA-1 thumbprint, as shown in the certificate's details) even if not trusted.</summary>
    public string? TrustedCertificateThumbprint { get; set; }

    public static DesktopSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "desktopsettings.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new()
            : new();
    }
}
