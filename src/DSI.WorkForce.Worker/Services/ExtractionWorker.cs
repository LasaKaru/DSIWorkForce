using System.Collections.Concurrent;
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
/// Continuous, bounded processing pipeline.
///
///  * Files: up to 2 x parallelism files are "in flight"; as soon as one finishes the next is claimed (no waiting for
///    a whole batch, no head-of-line blocking by one big PDF).
///  * Pages: a global pool of page slots (= parallelism) is shared by all files, so the pages of one 50-page PDF are
///    spread over all CPU cores, and the total work in memory is always bounded.
///  * Start: woken instantly by new uploads / hot-folder files (WorkerSignals); DB polling is only the safety net.
///  * Stability: each page commits in its own transaction (idempotent retries), PDF rendering is serialised (PDFium
///    is not thread-safe), a memory guard pauses new pages when memory is high, leases are renewed in the background,
///    failures back off and are parked after MaxAttempts.
/// </summary>
public sealed class ExtractionWorker(
    ScanQueueRepository queue,
    WorksheetRepository worksheets,
    ReferenceDataCache reference,
    DiagnosticsRepository diagnostics,
    LicenseService licensing,
    FormExtractor extractor,
    WorkerSignals signals,
    IOptions<WorkerOptions> options,
    IOptions<ExtractionOptions> extractionOptions,
    ILogger<ExtractionWorker> log) : BackgroundService
{
    private static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0";
    private readonly WorkerOptions _o = options.Value;
    private readonly ConcurrentDictionary<long, Task> _inFlight = new();
    private int _pagesSinceHeartbeat;
    private DateTime _lastLicenseWarning = DateTime.MinValue, _lastMemoryWarning = DateTime.MinValue;
    private SemaphoreSlim _pageSlots = null!;
    private int _degree;
    private long _memoryLimitBytes;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // We parallelise pages ourselves; stop OpenCV from also spawning a thread per core per page.
        Cv2.SetNumThreads(1);

        var ocrThreads = Math.Max(1, extractionOptions.Value.Local.CpuThreads);
        _degree = _o.MaxDegreeOfParallelism > 0 ? _o.MaxDegreeOfParallelism : Math.Clamp(Environment.ProcessorCount / ocrThreads, 1, 8);
        _pageSlots = new SemaphoreSlim(_degree, _degree);
        _memoryLimitBytes = _o.MaxMemoryMb > 0
            ? _o.MaxMemoryMb * 1024L * 1024L
            : (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes * 0.75);
        var maxFiles = _degree * 2;

        log.LogInformation("Extraction worker {Worker} started: {Pages} page(s) in parallel, up to {Files} file(s) in flight, memory limit {Mb} MB",
            _o.WorkerName, _degree, maxFiles, _memoryLimitBytes / 1024 / 1024);

        // Load the OCR model now so the first uploaded sheet is not slowed down by it.
        _ = Task.Run(() =>
        {
            try { extractor.WarmUp(Math.Min(_degree, 2)); log.LogInformation("OCR engine ready"); }
            catch (Exception ex) { log.LogWarning(ex, "OCR warm-up failed - it will load on first use"); }
        }, stoppingToken);
        var heartbeat = HeartbeatLoopAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
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
                    await signals.QueueHasWork.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                var free = maxFiles - _inFlight.Count;
                if (free > 0)
                {
                    var batch = await queue.ClaimBatchAsync(_o.WorkerName, Math.Min(free, _o.BatchSize), TimeSpan.FromMinutes(_o.LeaseMinutes), stoppingToken);
                    foreach (var file in batch)
                        _inFlight[file.ScanFileId] = Task.Run(async () =>
                        {
                            try { await ProcessFileAsync(file, stoppingToken); }
                            finally
                            {
                                _inFlight.TryRemove(file.ScanFileId, out _);
                                signals.QueueHasWork.Pulse();   // a slot is free: claim the next file right away
                            }
                        }, CancellationToken.None);
                    if (batch.Count == free) continue;   // there may be more waiting: go round again immediately
                }

                // Wait until a file finishes, new work is signalled, or the safety-net poll interval passes.
                await signals.QueueHasWork.WaitAsync(TimeSpan.FromSeconds(_o.PollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Database restart, network blip... wait and carry on; never let the service die.
                log.LogError(ex, "Worker loop error - retrying in 30 s");
                try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }

        // Let running pages finish their current step (their leases expire otherwise and another run resumes them).
        try { await Task.WhenAll(_inFlight.Values).WaitAsync(TimeSpan.FromSeconds(20)); } catch { /* shutting down */ }
        await heartbeat.ContinueWith(_ => { });
        log.LogInformation("Extraction worker stopped");
    }

    private async Task ProcessFileAsync(ScanFileRow file, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var scope = log.BeginScope(new Dictionary<string, object> { ["ScanFileId"] = file.ScanFileId, ["File"] = file.FileName });
        using var leaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var leaseLoop = RenewLeaseLoopAsync(file.ScanFileId, leaseCts.Token);
        try
        {
            var (template, fields, master) = await reference.GetAsync(file.TemplateCode, ct);
            var bytes = await File.ReadAllBytesAsync(file.StoredPath, ct);
            var isPdf = file.StoredPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
            var pageCount = isPdf ? PdfPageRenderer.GetPageCount(bytes) : 1;
            var done = await queue.GetProcessedPagesAsync(file.ScanFileId, ct);
            var referenceDate = DateOnly.FromDateTime(file.ReceivedAt.ToLocalTime());
            var todo = Enumerable.Range(1, pageCount).Where(p => !done.Contains(p)).ToList();   // skip pages saved by an interrupted run

            await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = _degree, CancellationToken = ct }, async (p, pct) =>
            {
                await _pageSlots.WaitAsync(pct);   // global limit across all files
                try
                {
                    await WaitForMemoryAsync(pct);
                    using var page = isPdf ? PdfPageRenderer.RenderPage(bytes, p - 1, template.Alignment.RenderDpi) : PdfPageRenderer.LoadImage(bytes);
                    var ws = await extractor.ExtractAsync(page, p, template, master, referenceDate, pct);
                    var imagePath = await SaveImageAsync(file, p, ws.AlignedImageJpeg!, pct);
                    var id = await worksheets.SaveExtractionAsync(file.ScanFileId, ws, imagePath, fields, pct);
                    Interlocked.Increment(ref _pagesSinceHeartbeat);
                    log.LogInformation("Page {Page}/{Pages} -> Worksheet {Id}: {Status}, {Issues} issue(s), min confidence {Conf:P0}",
                        p, pageCount, id, ws.Status, ws.Issues.Count, ws.MinConfidence);
                }
                finally { _pageSlots.Release(); }
            });

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
            try
            {
                var parked = await queue.FailAsync(file.ScanFileId, ex.Message, file.Attempts, _o.MaxAttempts, CancellationToken.None);
                if (parked) log.LogError(ex, "File {File} failed {Attempts} times - parked as Failed", file.FileName, file.Attempts);
                else log.LogWarning(ex, "File {File} failed (attempt {Attempts}) - will retry", file.FileName, file.Attempts);
            }
            catch (Exception dbEx) { log.LogError(dbEx, "Could not record failure of {File}; its lease will expire and it will be retried", file.FileName); }
        }
        finally
        {
            leaseCts.Cancel();
            await leaseLoop.ContinueWith(_ => { });
        }
    }

    /// <summary>Keeps the claim on a long file alive while its pages are processed.</summary>
    private async Task RenewLeaseLoopAsync(long scanFileId, CancellationToken ct)
    {
        var every = TimeSpan.FromMinutes(Math.Max(1, _o.LeaseMinutes / 3.0));
        try
        {
            while (true)
            {
                await Task.Delay(every, ct);
                try { await queue.ExtendLeaseAsync(scanFileId, TimeSpan.FromMinutes(_o.LeaseMinutes), ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Lease renewal failed for ScanFile {Id}", scanFileId); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await queue.HeartbeatAsync(_o.WorkerName, Version, Interlocked.Exchange(ref _pagesSinceHeartbeat, 0), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug(ex, "Heartbeat failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Memory guard: pauses new pages while the process is above the limit, instead of crashing with out-of-memory.</summary>
    private async Task WaitForMemoryAsync(CancellationToken ct)
    {
        for (var i = 0; Environment.WorkingSet > _memoryLimitBytes && i < 240; i++)
        {
            if (i == 0) GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            if (DateTime.UtcNow - _lastMemoryWarning > TimeSpan.FromMinutes(1))
            {
                log.LogWarning("Memory high ({Mb} MB) - pausing new pages until it drops", Environment.WorkingSet / 1024 / 1024);
                _lastMemoryWarning = DateTime.UtcNow;
            }
            await Task.Delay(500, ct);
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
