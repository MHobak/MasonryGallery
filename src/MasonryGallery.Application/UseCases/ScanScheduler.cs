namespace MasonryGallery.Application.UseCases;

using MasonryGallery.Application.Ports;
using MasonryGallery.Domain;
using Microsoft.Extensions.Logging;

/// <summary>Whether a finished scan walked the whole library or only changed paths.</summary>
public enum ScanKind
{
    Full,
    Paths
}

public sealed record ScanCompleted(LibraryId LibraryId, ScanKind Kind, Result Result);

/// <summary>
/// The one place scans start. Runs at most one scan per library at a time (libraries run in parallel)
/// and merges requests that arrive meanwhile: a pending full scan absorbs path requests, paths accumulate.
/// </summary>
public sealed class ScanScheduler : IDisposable
{
    private static readonly TimeSpan RetryDeferredAfter = TimeSpan.FromSeconds(2);

    private readonly ILibraryScanner _scanner;
    private readonly IClock _clock;
    private readonly ILogger<ScanScheduler> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _lock = new();
    private readonly Dictionary<int, Queue> _queues = [];

    private sealed class Queue
    {
        public bool Running;
        public bool FullPending;
        public readonly HashSet<string> Paths = new(StringComparer.Ordinal);
        public Task Loop = Task.CompletedTask;
    }

    public ScanScheduler(ILibraryScanner scanner, IClock clock, ILogger<ScanScheduler> logger)
    {
        _scanner = scanner;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Raised on a worker thread after every scan.</summary>
    public event Action<ScanCompleted>? Completed;

    /// <summary>Raised when <see cref="IsBusy"/> may have changed.</summary>
    public event Action? BusyChanged;

    public bool IsBusy
    {
        get { lock (_lock) return _queues.Values.Any(q => q.Running); }
    }

    public bool IsScanning(LibraryId id)
    {
        lock (_lock) return _queues.TryGetValue(id.Value, out var q) && q.Running;
    }

    public void RequestFullScan(LibraryId id)
    {
        bool started;
        lock (_lock)
        {
            var q = QueueFor(id);
            q.FullPending = true;
            q.Paths.Clear();
            started = EnsureRunning(id, q);
        }
        // Events are raised outside the lock: a handler may wait on the UI thread, which may be waiting for this lock.
        if (started)
            BusyChanged?.Invoke();
    }

    public void RequestPaths(LibraryId id, IEnumerable<string> relPaths)
    {
        var started = false;
        lock (_lock)
        {
            var q = QueueFor(id);
            if (!q.FullPending)
                q.Paths.UnionWith(relPaths);
            if (q.FullPending || q.Paths.Count > 0)
                started = EnsureRunning(id, q);
        }
        if (started)
            BusyChanged?.Invoke();
    }

    /// <summary>Completes when no scan is running or queued (deferred retries excluded).</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] loops;
            lock (_lock) loops = _queues.Values.Where(q => q.Running).Select(q => q.Loop).ToArray();
            if (loops.Length == 0)
                return;
            await Task.WhenAll(loops);
        }
    }

    private Queue QueueFor(LibraryId id)
    {
        if (!_queues.TryGetValue(id.Value, out var q))
            _queues[id.Value] = q = new Queue();
        return q;
    }

    // Called under the lock. Returns true when a loop was started (the caller raises BusyChanged after unlocking).
    private bool EnsureRunning(LibraryId id, Queue q)
    {
        if (q.Running || _shutdown.IsCancellationRequested)
            return false;
        q.Running = true;
        q.Loop = Task.Run(() => RunAsync(id, q));
        return true;
    }

    private async Task RunAsync(LibraryId id, Queue q)
    {
        var ct = _shutdown.Token;
        while (true)
        {
            ScanKind kind;
            string[] paths;
            lock (_lock)
            {
                if (ct.IsCancellationRequested || (!q.FullPending && q.Paths.Count == 0))
                {
                    q.Running = false;
                    break;
                }
                kind = q.FullPending ? ScanKind.Full : ScanKind.Paths;
                paths = kind == ScanKind.Full ? [] : [.. q.Paths];
                q.FullPending = false;
                q.Paths.Clear();
            }

            Result result;
            try
            {
                if (kind == ScanKind.Full)
                {
                    result = await _scanner.ExecuteAsync(id, ct);
                }
                else
                {
                    var outcome = await _scanner.ExecutePathsAsync(id, paths, ct);
                    result = outcome.IsSuccess ? Result.Success() : Result.Failure(outcome.Error!);
                    if (outcome.IsSuccess && outcome.Value!.Count > 0)
                        _ = RetryLaterAsync(id, outcome.Value, ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "{Kind} scan of library {LibraryId} failed", kind, id.Value);
                result = Result.Failure(ex.Message);
            }
            catch (OperationCanceledException)
            {
                result = Result.Failure("Cancelled");
            }

            try
            {
                Completed?.Invoke(new ScanCompleted(id, kind, result));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scan completion handler failed");
            }
        }
        BusyChanged?.Invoke();
    }

    // Files still being written come back once they have settled.
    private async Task RetryLaterAsync(LibraryId id, IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        try
        {
            await _clock.Delay(RetryDeferredAfter, ct);
            RequestPaths(id, paths);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Cancels running scans; they stop at their next checkpoint and leave the index consistent.</summary>
    public void Dispose() => _shutdown.Cancel(); // not disposed: loops and retries may still read the token
}
