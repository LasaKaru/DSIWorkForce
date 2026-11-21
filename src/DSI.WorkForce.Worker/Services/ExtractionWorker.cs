using System.Diagnostics;
using System.Reflection;
using DSI.WorkForce.Data;
using DSI.WorkForce.Data.Repositories;
using DSI.WorkForce.Extraction;
using DSI.WorkForce.Extraction.Imaging;
using Microsoft.Extensions.Options;
using OpenCvSharp;

namespace DSI.WorkForce.Worker.Services;

/// <summary>
/// Pulls queued files from SQL Server and processes pages with bounded parallelism.
/// Stable at high volume because: work is claimed in small leased batches, pages are rendered one at a
/// time and disposed, each page commits in its own transaction (idempotent on retry), failures back off
/// and are parked after MaxAttempts instead of blocking the queue.
/// </summary>
public sealed class ExtractionWorker(
    ScanQueueRepository queue,
    WorksheetRepository worksheets,
    ReferenceDataCache reference,
    DiagnosticsRepository diagnostics,
    LicenseService licensing,
    FormExtractor extractor,
    IOptions<WorkerOptions> options,
    ILogger<ExtractionWorker> log) : BackgroundService
{
    private static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0";
    private readonly WorkerOptions _o = options.Value;
    private int _pagesSinceHeartbeat;
    private DateTime _lastLicenseWarning = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Parallelism is done per file by us; stop OpenCV from also spawning a thread per core per page.
        Cv2.SetNumThreads(1);
        log.LogInformation("Extraction worker {Worker} started (parallelism {P})", _o.WorkerName, _o.MaxDegreeOfParallelism);
        var lastBeat = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - lastBeat > TimeSpan.FromSeconds(30))
                {
                    await queue.HeartbeatAsync(_o.WorkerName, Version, Interlocked.Exchange(ref _pagesSinceHeartbeat, 0), stoppingToken);
                    lastBeat = DateTime.UtcNow;
                }

                var license = await licensing.GetAsync(stoppingToken);
                if (!license.IsValid)
                {
                    // Product not activated / expired: do no work. Scans wait safely until a key is entered on the web.
                    if (DateTime.UtcNow - _lastLicenseWarning > TimeSpan.FromMinutes(5))
                    {
                        log.LogWarning("Processing paused - {Message} Activate the product on the web (License page) or contact {Mail}.",
                            license.Message, LicenseService.SupportEmail);
                        _lastLicenseWarning = DateTime.UtcNow;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                var batch = await queue.ClaimBatchAsync(_o.WorkerName, _o.BatchSize, TimeSpan.FromMinutes(_o.LeaseMinutes), stoppingToken);
                if (batch.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_o.PollIntervalSeconds), stoppingToken);
                    continue;
                }

                await Parallel.ForEachAsync(batch,
                    new ParallelOptions { MaxDegreeOfParallelism = _o.MaxDegreeOfParallelism, CancellationToken = stoppingToken },
                    async (file, ct) => await ProcessFileAsync(file, ct));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Database restart, network blip... wait and carry on; never let the service die.
                log.LogError(ex, "Worker loop error - retrying in 30 s");
                try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
        log.LogInformation("Extraction worker stopped");
    }

    private async Task ProcessFileAsync(ScanFileRow file, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var scope = log.BeginScope(new Dictionary<string, object> { ["ScanFileId"] = file.ScanFileId, ["File"] = file.FileName });
        try
        {
            var (template, fields, master) = await reference.GetAsync(file.TemplateCode, ct);
            var bytes = await File.ReadAllBytesAsync(file.StoredPath, ct);
            var isPdf = file.StoredPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
            var pageCount = isPdf ? PdfPageRenderer.GetPageCount(bytes) : 1;
            var done = await queue.GetProcessedPagesAsync(file.ScanFileId, ct);
            var referenceDate = DateOnly.FromDateTime(file.ReceivedAt.ToLocalTime());

            for (var p = 1; p <= pageCount; p++)
            {
                if (done.Contains(p)) continue;   // already saved by an earlier (interrupted) attempt
                using var page = isPdf ? PdfPageRenderer.RenderPage(bytes, p - 1, template.Alignment.RenderDpi) : PdfPageRenderer.LoadImage(bytes);
                var ws = await extractor.ExtractAsync(page, p, template, master, referenceDate, ct);

                var imagePath = await SaveImageAsync(file, p, ws.AlignedImageJpeg!, ct);
                var id = await worksheets.SaveExtractionAsync(file.ScanFileId, ws, imagePath, fields, ct);
                Interlocked.Increment(ref _pagesSinceHeartbeat);
                log.LogInformation("Page {Page}/{Pages} -> Worksheet {Id}: {Status}, {Issues} issue(s), min confidence {Conf:P0}",
                    p, pageCount, id, ws.Status, ws.Issues.Count, ws.MinConfidence);

                if (p % 10 == 0) await queue.ExtendLeaseAsync(file.ScanFileId, TimeSpan.FromMinutes(_o.LeaseMinutes), ct);
            }

            await queue.CompleteAsync(file.ScanFileId, pageCount, null, ct);
            log.LogInformation("Completed {File} ({Pages} page(s)) in {Ms} ms", file.FileName, pageCount, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Service stopping: the lease expires and another run resumes from the first unsaved page.
        }
        catch (Exception ex)
        {
            if (ex is Microsoft.Data.SqlClient.SqlException { Number: -2 })   // command timeout: show who holds the locks
                await LogBlockingAsync();
            var parked = await queue.FailAsync(file.ScanFileId, ex.Message, file.Attempts, _o.MaxAttempts, CancellationToken.None);
            if (parked) log.LogError(ex, "File {File} failed {Attempts} times - parked as Failed", file.FileName, file.Attempts);
            else log.LogWarning(ex, "File {File} failed (attempt {Attempts}) - will retry", file.FileName, file.Attempts);
        }
    }

    private async Task LogBlockingAsync()
    {
        try
        {
            var report = await diagnostics.GetLockReportAsync(CancellationToken.None);
            log.LogWarning("SQL timeout diagnostics (sessions with open transactions / blocking):{NewLine}{Report}", Environment.NewLine, report);
        }
        catch (Exception dx) { log.LogDebug(dx, "Could not read SQL diagnostics (needs VIEW SERVER STATE)"); }
    }

    private async Task<string> SaveImageAsync(ScanFileRow file, int page, byte[] jpeg, CancellationToken ct)
    {
        var d = file.ReceivedAt;
        var dir = Path.Combine(_o.ImageRoot, d.ToString("yyyy"), d.ToString("MM"), d.ToString("dd"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{file.ScanFileId}_{page}.jpg");
        await File.WriteAllBytesAsync(path, jpeg, ct);
        return path;
    }
}
