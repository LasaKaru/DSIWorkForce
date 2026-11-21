namespace DSI.WorkForce.Worker.Services;

/// <summary>
/// "Something new may be waiting" pulse between services in this process, so work starts immediately
/// instead of at the next poll. Polling stays as a safety net (other workers, missed file events).
/// </summary>
public sealed class WorkSignal
{
    private readonly SemaphoreSlim _sem = new(0, 1);

    public void Pulse()
    {
        try { if (_sem.CurrentCount == 0) _sem.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    /// <returns>true when pulsed, false when the timeout elapsed.</returns>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _sem.WaitAsync(timeout, ct);
}

/// <summary>Signals used by the Worker: new files in a hot folder, new rows in the queue.</summary>
public sealed class WorkerSignals
{
    public WorkSignal HotFolderChanged { get; } = new();
    public WorkSignal QueueHasWork { get; } = new();
}
