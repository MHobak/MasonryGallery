namespace MasonryGallery.Application.UseCases;

using MasonryGallery.Application.Ports;
using MasonryGallery.Application.Settings;
using MasonryGallery.Domain;
using Microsoft.Extensions.Logging;

public enum WatchMode
{
    /// <summary>A file system watcher reports changes within seconds.</summary>
    Live,
    /// <summary>No watcher possible (network share, inotify limit): changes show up at the next periodic rescan.</summary>
    Polling,
    /// <summary>Not watched right now: scanning for the first time, or the folder is offline.</summary>
    Off
}

public sealed record WatchStatus(WatchMode Mode, string? Reason = null);

/// <param name="Reconcile">Full rescan interval for watched libraries; the watcher is a hint, the rescan is the truth.</param>
/// <param name="Polling">Full rescan interval when live watching is unavailable.</param>
/// <param name="OfflineCheck">How often an offline library's folder is looked for.</param>
/// <param name="Tick">How often the schedule is checked.</param>
public sealed record MonitorOptions(TimeSpan Reconcile, TimeSpan Polling, TimeSpan OfflineCheck, TimeSpan Tick)
{
    public static MonitorOptions Default { get; } =
        new(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30));
}

/// <summary>
/// Keeps every enabled library current (design §5.4): a full scan at startup, a live watcher feeding
/// targeted rescans, periodic full reconciles, and automatic recovery when an offline folder returns.
/// </summary>
/// <remarks>
/// Locking rule: <see cref="_lock"/> only guards the entry table. Scheduler calls (which raise events whose
/// handlers may wait on the UI thread), watcher start-up (one inotify watch per folder) and watcher disposal
/// all happen after it is released. The UI thread reads <see cref="Statuses"/>, so holding the lock across
/// any of those could freeze or deadlock the window.
/// </remarks>
public sealed class LibraryMonitor : IDisposable
{
    private readonly ILibraryRepository _libraries;
    private readonly IFileWatcherFactory _watchers;
    private readonly IFileEnumerator _files;
    private readonly ScanScheduler _scheduler;
    private readonly IClock _clock;
    private readonly ILogger<LibraryMonitor> _logger;
    private readonly MonitorOptions _options;
    private readonly Lock _lock = new();
    private readonly Dictionary<int, Entry> _entries = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly ISettingsStore? _settings;

    private sealed class Entry(Library library)
    {
        public Library Library = library;
        public IFileWatcher? Watcher;
        public IDisposable? Subscription;
        public bool Starting;
        public bool Removed;
        public WatchStatus Status = new(WatchMode.Off, "Scanning");
        /// <summary>When the next full rescan is due; MaxValue while one is queued or running.</summary>
        public DateTime NextReconcile = DateTime.MaxValue;
    }

    public LibraryMonitor(
        ILibraryRepository libraries,
        IFileWatcherFactory watchers,
        IFileEnumerator files,
        ScanScheduler scheduler,
        IClock clock,
        ILogger<LibraryMonitor> logger,
        MonitorOptions? options = null,
        ISettingsStore? settings = null)
    {
        _libraries = libraries;
        _watchers = watchers;
        _files = files;
        _scheduler = scheduler;
        _clock = clock;
        _logger = logger;
        _options = options ?? MonitorOptions.Default;
        _scheduler.Completed += OnScanCompleted;
        _settings = settings;
        if (_settings is not null)
            _settings.Changed += OnSettingsChanged;
    }

    // Changed patterns can hide files that are indexed or reveal ones that aren't; only a full walk settles both.
    private void OnSettingsChanged(AppSettings before, AppSettings after)
    {
        if (before.IgnorePatterns.SequenceEqual(after.IgnorePatterns))
            return;
        List<LibraryId> ids;
        lock (_lock)
            ids = _entries.Values.Select(e => e.Library.Id).ToList();
        _logger.LogInformation("Ignore patterns changed; rescanning {Count} libraries", ids.Count);
        ids.ForEach(_scheduler.RequestFullScan);
    }

    /// <summary>Raised when any library's <see cref="WatchStatus"/> may have changed.</summary>
    public event Action? StatusChanged;

    public IReadOnlyDictionary<LibraryId, WatchStatus> Statuses
    {
        get { lock (_lock) return _entries.ToDictionary(e => new LibraryId(e.Key), e => e.Value.Status); }
    }

    /// <summary>Scans every enabled library once and starts the periodic schedule.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await SyncAsync(ct);
        _ = RunScheduleAsync(_stop.Token);
    }

    /// <summary>
    /// Re-reads the library list after it changed: new or re-enabled libraries are scanned and watched,
    /// removed or disabled ones are released.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        var enabled = (await _libraries.ListAsync(ct)).Where(l => l.Enabled).ToList();
        var released = new List<Entry>();
        var added = new List<Entry>();
        lock (_lock)
        {
            foreach (var id in _entries.Keys.Except(enabled.Select(l => l.Id.Value)).ToList())
            {
                released.Add(Detach(_entries[id]));
                _entries[id].Removed = true;
                _entries.Remove(id);
            }
            foreach (var library in enabled)
            {
                if (_entries.TryGetValue(library.Id.Value, out var known))
                    known.Library = library;
                else
                    added.Add(_entries[library.Id.Value] = new Entry(library));
            }
        }

        released.ForEach(Dispose);
        foreach (var entry in added)
        {
            // Watch before the first scan finishes, so changes made during it are not lost.
            StartWatcher(entry);
            _scheduler.RequestFullScan(entry.Library.Id);
        }
        StatusChanged?.Invoke();
    }

    private async Task RunScheduleAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.Tick);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                Tick();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Starts every full rescan that is due. Called by the schedule; tests call it directly.</summary>
    internal void Tick()
    {
        var now = _clock.UtcNow;
        var due = new List<LibraryId>();
        lock (_lock)
        {
            foreach (var entry in _entries.Values)
            {
                if (now < entry.NextReconcile || _scheduler.IsScanning(entry.Library.Id))
                    continue;
                // An offline folder costs one stat per check until it is back; then a full scan restores it.
                if (entry.Library.Status == LibraryStatus.Offline && !_files.RootExists(entry.Library.RootPath))
                {
                    entry.NextReconcile = now + _options.OfflineCheck;
                    continue;
                }
                entry.NextReconcile = DateTime.MaxValue;
                due.Add(entry.Library.Id);
            }
        }
        due.ForEach(_scheduler.RequestFullScan);
    }

    private void OnScanCompleted(ScanCompleted done) => _ = OnScanCompletedAsync(done);

    private async Task OnScanCompletedAsync(ScanCompleted done)
    {
        Result<Library> library;
        try
        {
            library = await _libraries.GetByIdAsync(done.LibraryId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reload library {LibraryId} after a scan", done.LibraryId.Value);
            return;
        }

        Entry? entry;
        Entry? released = null;
        lock (_lock)
        {
            if (!library.IsSuccess || !_entries.TryGetValue(done.LibraryId.Value, out entry))
                return; // removed or disabled meanwhile
            entry.Library = library.Value!;

            if (entry.Library.Status == LibraryStatus.Offline)
            {
                released = Detach(entry);
                entry.Status = new WatchStatus(WatchMode.Off, "Folder not available");
                entry.NextReconcile = _clock.UtcNow + _options.OfflineCheck;
            }
            // A full rescan is also the moment to retry live watching (e.g. inotify limits were raised).
            else if (done.Kind == ScanKind.Full && entry.Status.Mode == WatchMode.Polling)
            {
                entry.Status = new WatchStatus(WatchMode.Off);
            }
        }

        if (released is not null)
        {
            Dispose(released);
        }
        else
        {
            StartWatcher(entry);
            if (done.Kind == ScanKind.Full)
            {
                lock (_lock)
                    entry.NextReconcile = _clock.UtcNow + (entry.Status.Mode == WatchMode.Live ? _options.Reconcile : _options.Polling);
            }
        }
        StatusChanged?.Invoke();
    }

    private void StartWatcher(Entry entry)
    {
        string root;
        lock (_lock)
        {
            if (entry.Removed || entry.Starting || entry.Watcher is not null || entry.Status.Mode == WatchMode.Polling)
                return;
            entry.Starting = true;
            root = entry.Library.RootPath;
        }

        // Registering a watch per folder can take a while on a big tree; nobody waits on the lock meanwhile.
        var watcher = _files.RootExists(root) ? _watchers.TryWatch(root) : null;

        lock (_lock)
        {
            entry.Starting = false;
            if (watcher is null)
                return;
            if (entry.Removed)
            {
                if (watcher.IsSuccess) watcher.Value!.Dispose();
                return;
            }
            if (!watcher.IsSuccess)
            {
                _logger.LogWarning("Library {LibraryId} is polled instead of watched: {Reason}", entry.Library.Id.Value, watcher.Error);
                entry.Status = new WatchStatus(WatchMode.Polling, watcher.Error);
                return;
            }
            var id = entry.Library.Id;
            entry.Watcher = watcher.Value!;
            entry.Subscription = entry.Watcher.Signals.Subscribe(signal => OnSignal(id, signal));
            entry.Status = new WatchStatus(WatchMode.Live);
        }
    }

    private void OnSignal(LibraryId id, WatchSignal signal)
    {
        switch (signal)
        {
            case WatchSignal.Changed changed:
                _scheduler.RequestPaths(id, changed.Paths);
                break;
            case WatchSignal.Overflow:
                _logger.LogInformation("Watcher for library {LibraryId} lost events; rescanning", id.Value);
                _scheduler.RequestFullScan(id);
                break;
            case WatchSignal.Failed failed:
                Entry released;
                lock (_lock)
                {
                    if (!_entries.TryGetValue(id.Value, out var entry))
                        return;
                    released = Detach(entry);
                    entry.Status = new WatchStatus(WatchMode.Polling, failed.Reason);
                }
                Dispose(released);
                _logger.LogWarning("Watcher for library {LibraryId} failed, polling instead: {Reason}", id.Value, failed.Reason);
                StatusChanged?.Invoke();
                // Changes may have been missed while the watcher was failing.
                _scheduler.RequestFullScan(id);
                break;
        }
    }

    // Under the lock: takes the watcher off the entry. Returns a holder to dispose once the lock is released.
    private static Entry Detach(Entry entry)
    {
        var holder = new Entry(entry.Library) { Watcher = entry.Watcher, Subscription = entry.Subscription };
        entry.Watcher = null;
        entry.Subscription = null;
        return holder;
    }

    private static void Dispose(Entry holder)
    {
        holder.Subscription?.Dispose();
        holder.Watcher?.Dispose();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _scheduler.Completed -= OnScanCompleted;
        if (_settings is not null)
            _settings.Changed -= OnSettingsChanged;
        List<Entry> released;
        lock (_lock)
        {
            released = _entries.Values.Select(e => { e.Removed = true; return Detach(e); }).ToList();
            _entries.Clear();
        }
        released.ForEach(Dispose);
    }
}
