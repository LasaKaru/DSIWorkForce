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
        var handler = new HttpClientHandler { UseDefaultCredentials = true, PreAuthenticate = true };
        _http = new HttpClient(handler) { BaseAddress = new Uri(settings.ApiBaseUrl), Timeout = TimeSpan.FromMinutes(5) };
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
