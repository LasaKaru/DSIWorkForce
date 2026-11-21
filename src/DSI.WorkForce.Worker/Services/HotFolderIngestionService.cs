using System.Security.Cryptography;
using DSI.WorkForce.Data.Repositories;
using Microsoft.Extensions.Options;

namespace DSI.WorkForce.Worker.Services;

/// <summary>
/// Polls the scanner hot folders (polling is more reliable than FileSystemWatcher on network shares),
/// de-duplicates by SHA-256 and enqueues files. Order is copy -> enqueue -> delete so a crash at any
/// point never loses or double-processes a sheet.
/// </summary>
public sealed class HotFolderIngestionService(ScanQueueRepository queue, IOptions<WorkerOptions> options, ILogger<HotFolderIngestionService> log)
    : BackgroundService
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff" };
    private readonly WorkerOptions _o = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_o.HotFolders.Count == 0) { log.LogWarning("No Worker:HotFolders configured - ingestion disabled."); return; }
        foreach (var f in _o.HotFolders)
        {
            // Local folders (e.g. C:\DSIWorkForce\hot\PC-011-A on a developer PC) are created automatically;
            // network shares (\\server\share) must already exist.
            if (!f.Path.StartsWith(@"\\", StringComparison.Ordinal))
                try { Directory.CreateDirectory(f.Path); } catch (Exception ex) { log.LogWarning(ex, "Cannot create {Path}", f.Path); }
            log.LogInformation("Watching {Path} for template {Template}", f.Path, f.TemplateCode);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_o.PollIntervalSeconds));
        do
        {
            foreach (var folder in _o.HotFolders)
            {
                try { await ScanFolderAsync(folder, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Hot folder {Path} scan failed", folder.Path);   // e.g. share offline: retry next tick
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ScanFolderAsync(HotFolderOptions folder, CancellationToken ct)
    {
        if (!Directory.Exists(folder.Path)) { log.LogWarning("Hot folder {Path} not reachable", folder.Path); return; }

        var cutoff = DateTime.UtcNow.AddSeconds(-_o.FileStableSeconds);
        var files = new DirectoryInfo(folder.Path).EnumerateFiles()
            .Where(f => Extensions.Contains(f.Extension) && f.LastWriteTimeUtc < cutoff)
            .OrderBy(f => f.LastWriteTimeUtc)
            .Take(500);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            string sha;
            try
            {
                await using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.None);   // fails while scanner still writes
                sha = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            }
            catch (IOException) { continue; }

            if (await queue.ExistsAsync(sha, ct))
            {
                MoveToDuplicates(file);
                continue;
            }

            var now = DateTime.Now;
            var destDir = Path.Combine(_o.StagingRoot, now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"));
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, $"{sha[..12]}_{file.Name}");
            file.CopyTo(dest, overwrite: true);

            var id = await queue.EnqueueAsync(file.Name, dest, sha, folder.TemplateCode, folder.Station ?? folder.Path, null, ct);
            if (id is null) { MoveToDuplicates(file); continue; }

            file.Delete();
            log.LogInformation("Queued {File} as ScanFile {Id} ({Template})", file.Name, id, folder.TemplateCode);
        }
    }

    private void MoveToDuplicates(FileInfo file)
    {
        Directory.CreateDirectory(_o.DuplicatesFolder);
        var target = Path.Combine(_o.DuplicatesFolder, $"{DateTime.Now:yyyyMMddHHmmss}_{file.Name}");
        file.MoveTo(target, overwrite: true);
        log.LogWarning("Duplicate scan {File} moved to {Target}", file.Name, target);
    }
}
