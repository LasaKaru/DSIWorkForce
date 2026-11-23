using Dapper;
using DSI.WorkForce.Core.Contracts;

namespace DSI.WorkForce.Data.Repositories;

public sealed record ScanFileRow(long ScanFileId, string FileName, string StoredPath, string TemplateCode, int Attempts, DateTime ReceivedAt);

/// <summary>
/// SQL Server table used as a durable queue. Claiming uses UPDLOCK + READPAST, so any number of worker
/// processes/threads can pull work concurrently, and a crashed worker's lease simply expires.
/// </summary>
public sealed class ScanQueueRepository(IDbConnectionFactory db)
{
    public async Task<bool> ExistsAsync(string sha256, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM dsi.ScanFile WHERE Sha256 = @sha256", new { sha256 }, cancellationToken: ct)) > 0;
    }

    /// <returns>New ScanFileId, or null when the same file (hash) was already imported.</returns>
    public async Task<long?> EnqueueAsync(string fileName, string storedPath, string sha256, string templateCode,
        string? station, string? uploadedBy, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long?>(new CommandDefinition("""
            BEGIN TRY
                INSERT dsi.ScanFile (FileName, StoredPath, Sha256, TemplateCode, SourceStation, UploadedBy)
                OUTPUT inserted.ScanFileId
                VALUES (@fileName, @storedPath, @sha256, @templateCode, @station, @uploadedBy);
            END TRY
            BEGIN CATCH
                IF ERROR_NUMBER() IN (2601, 2627) SELECT CAST(NULL AS BIGINT); ELSE THROW;
            END CATCH
            """, new { fileName, storedPath, sha256, templateCode, station, uploadedBy }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ScanFileRow>> ClaimBatchAsync(string workerName, int batchSize, TimeSpan lease, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ScanFileRow>(new CommandDefinition("""
            WITH next AS (
                SELECT TOP (@batchSize) *
                FROM dsi.ScanFile WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE (Status = N'Pending' AND NextAttemptAt <= SYSUTCDATETIME())
                   OR (Status = N'Processing' AND LockedUntil < SYSUTCDATETIME())   -- lease expired: worker died
                ORDER BY ReceivedAt
            )
            UPDATE next
               SET Status = N'Processing', LockedBy = @workerName,
                   LockedUntil = DATEADD(SECOND, @leaseSeconds, SYSUTCDATETIME()), Attempts = Attempts + 1
            OUTPUT inserted.ScanFileId, inserted.FileName, inserted.StoredPath, inserted.TemplateCode, inserted.Attempts, inserted.ReceivedAt;
            """, new { batchSize, workerName, leaseSeconds = (int)lease.TotalSeconds }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task ExtendLeaseAsync(long scanFileId, TimeSpan lease, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dsi.ScanFile SET LockedUntil = DATEADD(SECOND, @s, SYSUTCDATETIME()) WHERE ScanFileId = @scanFileId AND Status = N'Processing'",
            new { scanFileId, s = (int)lease.TotalSeconds }, cancellationToken: ct));
    }

    public async Task CompleteAsync(long scanFileId, int pageCount, string? archivedPath, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE dsi.ScanFile
               SET Status = N'Completed', PageCount = @pageCount, CompletedAt = SYSUTCDATETIME(),
                   LockedBy = NULL, LockedUntil = NULL, LastError = NULL,
                   StoredPath = COALESCE(@archivedPath, StoredPath)
             WHERE ScanFileId = @scanFileId;
            """, new { scanFileId, pageCount, archivedPath }, cancellationToken: ct));
    }

    /// <summary>Retries with exponential back-off; after <paramref name="maxAttempts"/> the file is parked as Failed.</summary>
    public async Task<bool> FailAsync(long scanFileId, string error, int attempts, int maxAttempts, CancellationToken ct = default)
    {
        var final = attempts >= maxAttempts;
        var delaySeconds = (int)Math.Min(3600, 30 * Math.Pow(2, attempts - 1));
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE dsi.ScanFile
               SET Status = CASE WHEN @final = 1 THEN N'Failed' ELSE N'Pending' END,
                   NextAttemptAt = DATEADD(SECOND, @delaySeconds, SYSUTCDATETIME()),
                   LockedBy = NULL, LockedUntil = NULL, LastError = LEFT(@error, 2000)
             WHERE ScanFileId = @scanFileId;
            """, new { scanFileId, error, final, delaySeconds }, cancellationToken: ct));
        return final;
    }

    public async Task<int> RetryFailedAsync(long? scanFileId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE dsi.ScanFile SET Status = N'Pending', Attempts = 0, NextAttemptAt = SYSUTCDATETIME()
            WHERE Status = N'Failed' AND (@scanFileId IS NULL OR ScanFileId = @scanFileId);
            """, new { scanFileId }, cancellationToken: ct));
    }

    public async Task<IReadOnlySet<int>> GetProcessedPagesAsync(long scanFileId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var pages = await conn.QueryAsync<int>(new CommandDefinition(
            "SELECT PageNo FROM dsi.Worksheet WHERE ScanFileId = @scanFileId", new { scanFileId }, cancellationToken: ct));
        return pages.ToHashSet();
    }

    public async Task HeartbeatAsync(string workerName, string version, int processedPages, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            MERGE dsi.WorkerHeartbeat AS t
            USING (SELECT @workerName AS WorkerName) AS s ON t.WorkerName = s.WorkerName
            WHEN MATCHED THEN UPDATE SET LastSeenUtc = SYSUTCDATETIME(), Version = @version, ProcessedPages = t.ProcessedPages + @processedPages
            WHEN NOT MATCHED THEN INSERT (WorkerName, LastSeenUtc, Version, ProcessedPages) VALUES (@workerName, SYSUTCDATETIME(), @version, @processedPages);
            """, new { workerName, version, processedPages }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<(long ScanFileId, string FileName, int Attempts, string? LastError, DateTime ReceivedAt)>> GetFailedAsync(int top, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(long, string, int, string?, DateTime)>(new CommandDefinition(
            "SELECT TOP (@top) ScanFileId, FileName, Attempts, LastError, ReceivedAt FROM dsi.ScanFile WHERE Status = N'Failed' ORDER BY ReceivedAt DESC",
            new { top }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Processing status of files uploaded from the web (looked up by their hot-folder name).</summary>
    public async Task<IReadOnlyList<ScanStatusDto>> GetStatusByFileNamesAsync(IReadOnlyCollection<string> storedNames, CancellationToken ct = default)
    {
        if (storedNames.Count == 0) return [];
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ScanStatusDto>(new CommandDefinition("""
            SELECT sf.FileName AS StoredName, sf.ScanFileId, sf.Status, sf.LastError,
                   (SELECT COUNT(*) FROM dsi.Worksheet w WHERE w.ScanFileId = sf.ScanFileId) AS Pages,
                   (SELECT COUNT(*) FROM dsi.Worksheet w WHERE w.ScanFileId = sf.ScanFileId AND w.Status = N'NeedsReview') AS NeedsReview,
                   (SELECT MIN(w.WorksheetId) FROM dsi.Worksheet w WHERE w.ScanFileId = sf.ScanFileId) AS FirstWorksheetId
            FROM dsi.ScanFile sf
            WHERE sf.FileName IN @storedNames
            """, new { storedNames }, cancellationToken: ct));
        return rows.ToList();
    }
}
