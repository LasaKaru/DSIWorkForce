using System.Security.Cryptography;
using DSI.WorkForce.Core.Contracts;
using DSI.WorkForce.Data.Repositories;
using Microsoft.Extensions.Options;

namespace DSI.WorkForce.Web.Services;

/// <summary>
/// Drops uploaded scans into the worker's hot folder (the Worker queues and processes them within seconds).
/// The file is written under a temporary name and renamed when complete, so the Worker never reads half a file.
/// Exact duplicates (same SHA-256 as an imported file) are rejected immediately.
/// </summary>
public sealed class UploadService(IOptions<UploadOptions> options, ScanQueueRepository queue, ILogger<UploadService> log)
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff" };

    public IReadOnlyCollection<string> TemplateCodes => options.Value.HotFolders.Keys;
    public long MaxFileBytes => options.Value.MaxFileBytes;
    public static IReadOnlyCollection<string> AllowedExtensions => Allowed;

    public async Task<UploadResultDto> SaveAsync(string templateCode, string fileName, Stream content, string user, CancellationToken ct,
        IProgress<long>? progress = null)
    {
        var safeName = Path.GetFileName(fileName);
        if (!Allowed.Contains(Path.GetExtension(safeName)))
            return new UploadResultDto(safeName, false, "Only PDF or image scans (PDF, JPG, PNG, TIFF) are accepted.");
        if (!options.Value.HotFolders.TryGetValue(templateCode, out var folder))
            return new UploadResultDto(safeName, false, $"Unknown form template '{templateCode}'.");

        Directory.CreateDirectory(folder);
        var stored = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..32] + "_" + safeName;
        var final = Path.Combine(folder, stored);
        var temp = final + ".uploading";
        string sha;
        try
        {
            await using (var fs = File.Create(temp))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                    total += read;
                    progress?.Report(total);
                }
                sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            if (await queue.ExistsAsync(sha, ct))
            {
                File.Delete(temp);
                return new UploadResultDto(safeName, false, "Already imported earlier (identical file) - skipped.", null, Duplicate: true);
            }
            File.Move(temp, final);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }

        log.LogInformation("{User} uploaded {File} as {Stored} for {Template}", user, safeName, stored, templateCode);
        return new UploadResultDto(safeName, true, "Uploaded - waiting for processing.", stored);
    }
}
