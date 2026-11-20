using DSI.WorkForce.Core.Contracts;
using Microsoft.Extensions.Options;

namespace DSI.WorkForce.Web.Services;

/// <summary>Drops uploaded scans into the worker's hot folder. The worker does hashing, de-duplication and queuing.</summary>
public sealed class UploadService(IOptions<UploadOptions> options, ILogger<UploadService> log)
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff" };

    public IReadOnlyCollection<string> TemplateCodes => options.Value.HotFolders.Keys;
    public long MaxFileBytes => options.Value.MaxFileBytes;

    public async Task<UploadResultDto> SaveAsync(string templateCode, string fileName, Stream content, string user, CancellationToken ct)
    {
        var safeName = Path.GetFileName(fileName);
        if (!Allowed.Contains(Path.GetExtension(safeName)))
            return new UploadResultDto(safeName, false, "Only PDF or image scans are accepted.");
        if (!options.Value.HotFolders.TryGetValue(templateCode, out var folder))
            return new UploadResultDto(safeName, false, $"Unknown form template '{templateCode}'.");

        Directory.CreateDirectory(folder);
        // Write under a temp name first so the worker never sees a half-written file.
        var final = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..32] + "_" + safeName);
        var temp = final + ".uploading";
        await using (var fs = File.Create(temp)) await content.CopyToAsync(fs, ct);
        File.Move(temp, final);
        log.LogInformation("{User} uploaded {File} for {Template}", user, safeName, templateCode);
        return new UploadResultDto(safeName, true, "Queued for processing.");
    }
}
