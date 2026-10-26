namespace MasonryGallery.Application.Pipeline;

/// <summary>
/// The user's "pause indexing" switch (design §5.5). Probe and thumbnail workers wait here before each item, so a
/// pause takes effect within one file per worker; finding files and noticing deletions carry on, they are cheap.
/// Not persisted: every start indexes again.
/// </summary>
public sealed class IndexingPause
{
    private readonly Lock _lock = new();
    private TaskCompletionSource? _resumed; // null while running

    public bool IsPaused
    {
        get { lock (_lock) return _resumed is not null; }
    }

    /// <summary>Raised after <see cref="IsPaused"/> changed (on the caller's thread, outside any lock).</summary>
    public event Action? Changed;

    public void Pause()
    {
        lock (_lock)
        {
            if (_resumed is not null) return;
            _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Changed?.Invoke();
    }

    public void Resume()
    {
        TaskCompletionSource? resumed;
        lock (_lock)
        {
            resumed = _resumed;
            _resumed = null;
        }
        if (resumed is null) return;
        resumed.SetResult();
        Changed?.Invoke();
    }

    /// <summary>Completes at once while running; while paused, when resumed or <paramref name="ct"/> is cancelled.</summary>
    public Task WaitWhilePausedAsync(CancellationToken ct)
    {
        Task? wait;
        lock (_lock) wait = _resumed?.Task;
        return wait is null ? Task.CompletedTask : wait.WaitAsync(ct);
    }
}
