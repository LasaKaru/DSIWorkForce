using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DSI.WorkForce.Core.Contracts;

namespace DSI.WorkForce.Desktop.Services;

/// <summary>Typed client for DSI.WorkForce.Web /api. Uses the logged-in Windows account (no passwords stored).</summary>
public sealed class ApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient _http;

    public ApiClient(DesktopSettings settings)
    {
        // The sign-in cookie returned by /api/auth/login is kept in this container for all later calls.
        var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
        var pinned = settings.TrustedCertificateThumbprint?.Replace(" ", "").Replace(":", "");
        CertificateCheckDisabled = settings.AllowUntrustedCertificate;
        handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            errors == System.Net.Security.SslPolicyErrors.None                                                    // trusted normally
            || (!string.IsNullOrEmpty(pinned) && cert is not null && string.Equals(cert.GetCertHashString(), pinned, StringComparison.OrdinalIgnoreCase))
            || settings.AllowUntrustedCertificate;                                                                // explicit bypass
        _http = new HttpClient(new LicenseAwareHandler { InnerHandler = handler })
        {
            BaseAddress = new Uri(settings.ApiBaseUrl), Timeout = TimeSpan.FromMinutes(5),
        };
    }

    public CurrentUserDto? User { get; private set; }

    /// <summary>True when desktopsettings.json switches the HTTPS certificate check off (shown as a warning).</summary>
    public bool CertificateCheckDisabled { get; }

    public bool Can(string permission) =>
        User is { } u && (u.Permissions.Contains(permission) || u.Permissions.Contains("Admin"));

    public async Task<string?> LoginAsync(string userName, string password)
    {
        using var resp = await _http.PostAsJsonAsync("api/auth/login", new LoginRequest(userName, password), Json);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            var body = await resp.Content.ReadAsStringAsync();
            return body.Contains("LockedOut") ? "Too many failed attempts. Try again in 15 minutes."
                 : body.Contains("Inactive") ? "This account is disabled." : "Wrong user name or password.";
        }
        resp.EnsureSuccessStatusCode();
        User = await resp.Content.ReadFromJsonAsync<CurrentUserDto>(Json);
        return null;
    }

    /// <summary>Copyright line for the windows, following the "Show Helao2 branding" setting (default when the server is unreachable).</summary>
    public async Task<string> GetCopyrightAsync()
    {
        try
        {
            var b = await _http.GetFromJsonAsync<BrandingSettings>("api/auth/branding", Json);
            if (b is { ShowVendorBranding: false }) return "© 2026 DSI WorkForce";
        }
        catch { /* older server or offline: keep the default */ }
        return "© 2026 Helao2 (Pvt) Ltd · support@helao2.com";
    }

    public async Task<DashboardDto> GetDashboardAsync() => (await _http.GetFromJsonAsync<DashboardDto>("api/dashboard", Json))!;

    public async Task<List<WorksheetSummaryDto>> GetWorksheetsAsync(string? status, int top = 500) =>
        (await _http.GetFromJsonAsync<List<WorksheetSummaryDto>>($"api/worksheets?status={Uri.EscapeDataString(status ?? "")}&top={top}", Json))!;

    public async Task<WorksheetDetailDto> GetWorksheetAsync(long id) => (await _http.GetFromJsonAsync<WorksheetDetailDto>($"api/worksheets/{id}", Json))!;

    public Task<byte[]> GetWorksheetImageAsync(long id) => _http.GetByteArrayAsync($"api/worksheets/{id}/image");

    public async Task SaveWorksheetAsync(long id, SaveCorrectionsRequest request)
    {
        using var resp = await _http.PutAsJsonAsync($"api/worksheets/{id}", request, Json);
        if (resp.StatusCode == HttpStatusCode.Conflict) throw new InvalidOperationException("Someone else changed this sheet. Reload it and try again.");
        resp.EnsureSuccessStatusCode();
    }

    public async Task RejectWorksheetAsync(long id, string reason)
    {
        using var resp = await _http.PostAsJsonAsync($"api/worksheets/{id}/reject", new { reason }, Json);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<List<ReportListItem>> GetReportsAsync() => (await _http.GetFromJsonAsync<List<ReportListItem>>("api/reports", Json))!;

    public async Task<ReportResultDto> RunReportAsync(string code, DateOnly from, DateOnly to, string? shift, string? resourceNo, string? epfNo) =>
        (await _http.GetFromJsonAsync<ReportResultDto>(ReportUrl(code, from, to, shift, resourceNo, epfNo), Json))!;

    public Task<byte[]> ExportReportAsync(string code, DateOnly from, DateOnly to, string? shift, string? resourceNo, string? epfNo) =>
        _http.GetByteArrayAsync(ReportUrl(code, from, to, shift, resourceNo, epfNo).Replace("?", "/export?"));

    public async Task<List<UploadResultDto>> UploadAsync(string templateCode, IEnumerable<string> files)
    {
        using var form = new MultipartFormDataContent { { new StringContent(templateCode), "templateCode" } };
        var streams = new List<Stream>();
        try
        {
            foreach (var f in files)
            {
                var s = File.OpenRead(f);
                streams.Add(s);
                var content = new StreamContent(s);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(content, "files", Path.GetFileName(f));
            }
            using var resp = await _http.PostAsync("api/scans", form);
            resp.EnsureSuccessStatusCode();
            return (await resp.Content.ReadFromJsonAsync<List<UploadResultDto>>(Json))!;
        }
        finally { foreach (var s in streams) s.Dispose(); }
    }

    private static string ReportUrl(string code, DateOnly from, DateOnly to, string? shift, string? resourceNo, string? epfNo) =>
        $"api/reports/{Uri.EscapeDataString(code)}?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}" +
        $"&shift={Uri.EscapeDataString(shift ?? "")}&resourceNo={Uri.EscapeDataString(resourceNo ?? "")}&epfNo={Uri.EscapeDataString(epfNo ?? "")}";
}

public sealed record ReportListItem(string Code, string Title, string? Subtitle);

/// <summary>Turns "402 license required" and "403 no permission" into readable messages.</summary>
public sealed class LicenseAwareHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var resp = await base.SendAsync(request, ct);
        if (resp.StatusCode == HttpStatusCode.PaymentRequired)
            throw new InvalidOperationException("DSI WorkForce is not activated or the license has expired. " +
                "An administrator must enter a license key on the web (License page). Contact support@helao2.com.");
        if (resp.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Your account does not have permission for this. Ask an administrator.");
        if (resp.StatusCode == HttpStatusCode.Unauthorized && !request.RequestUri!.AbsolutePath.EndsWith("/api/auth/login"))
            throw new InvalidOperationException("Your session has ended. Restart the application and sign in again.");
        return resp;
    }
}
