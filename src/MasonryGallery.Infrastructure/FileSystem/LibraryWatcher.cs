namespace MasonryGallery.Infrastructure.FileSystem;

using System.Reactive.Linq;
using System.Reactive.Subjects;
using MasonryGallery.Application.Ports;
using MasonryGallery.Domain;
using Microsoft.Extensions.Logging;

/// <param name="Quiet">A batch is sent once no event has arrived for this long.</param>
/// <param name="MaxDelay">...or at the latest this long after its first event, so a long copy still shows progress.</param>
public sealed record WatcherTiming(TimeSpan Quiet, TimeSpan MaxDelay)
{
    public static WatcherTiming Default { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
}

public sealed class FileWatcherFactory : IFileWatcherFactory
{
    private readonly ILoggerFactory _loggers;
    private readonly WatcherTiming _timing;

    public FileWatcherFactory(ILoggerFactory loggers, WatcherTiming? timing = null)
    {
        _loggers = loggers;
        _timing = timing ?? WatcherTiming.Default;
    }

    public Result<IFileWatcher> TryWatch(string rootPath)
    {
        // Remote edits don't raise notifications on the client, so a watcher there would just miss changes.
        if (VolumeInfo.IsNetwork(rootPath))
            return Result<IFileWatcher>.Failure("Network folder: changes are picked up every few minutes.");
        try
        {
            return new LibraryWatcher(rootPath, _timing, _loggers.CreateLogger<LibraryWatcher>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Result<IFileWatcher>.Failure(LibraryWatcher.Describe(ex));
        }
    }
}

/// <summary>One recursive <see cref="FileSystemWatcher"/> on a library root, reporting coalesced batches of paths.</summary>
internal sealed class LibraryWatcher : IFileWatcher
{
    private readonly FileSystemWatcher _watcher;
    private readonly ChangeBatcher _batcher;
    private readonly ISubject<WatchSignal> _signals = Subject.Synchronize(new Subject<WatchSignal>());
    private readonly ILogger _logger;
    private readonly string _root;
    private int _disposed;

    public LibraryWatcher(string rootPath, WatcherTiming timing, ILogger logger)
    {
        _logger = logger;
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        _batcher = new ChangeBatcher(timing, paths => Emit(new WatchSignal.Changed(paths)));
        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            // 64 KB is the documented maximum; the 8 KB default overflows on a burst of a few hundred events.
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
        };
        _watcher.Created += OnEvent;
        _watcher.Changed += OnEvent;
        _watcher.Deleted += OnEvent;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
        try
        {
            // On Linux this registers an inotify watch per folder and throws when the limit is reached.
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            _watcher.Dispose();
            _batcher.Dispose();
            throw;
        }
        _logger.LogInformation("Watching {Root}", _root);
    }

    public IObservable<WatchSignal> Signals => _signals.AsObservable();

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        // A folder "changes" whenever its contents do; the files involved arrive as their own events.
        if (e.ChangeType == WatcherChangeTypes.Changed && Directory.Exists(e.FullPath))
            return;
        Add(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        Add(e.OldFullPath); // shows up as missing: its rows are removed
        Add(e.FullPath);    // shows up as new: indexed
    }

    private void Add(string fullPath)
    {
        var relative = Path.GetRelativePath(_root, fullPath);
        // Outside the root (".." or "../x"); a name that merely starts with dots, like "..notes", is fine.
        var outside = relative.StartsWith("..", StringComparison.Ordinal)
                      && (relative.Length == 2 || relative[2] == '/' || relative[2] == '\\');
        if (outside || Path.IsPathRooted(relative))
            return;
        _batcher.Add(relative == "." ? "" : relative.Replace('\\', '/'));
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        WatchSignal signal = ex is InternalBufferOverflowException ? new WatchSignal.Overflow() : new WatchSignal.Failed(Describe(ex));
        _logger.LogWarning(ex, "Watcher error on {Root}", _root);
        // The subscriber may dispose this watcher in response; don't do that from inside the watcher's own callback.
        ThreadPool.QueueUserWorkItem(_ => Emit(signal));
    }

    private void Emit(WatchSignal signal)
    {
        if (Volatile.Read(ref _disposed) == 0)
            _signals.OnNext(signal);
    }

    internal static string Describe(Exception ex)
        => ex.Message.Contains("inotify", StringComparison.OrdinalIgnoreCase)
            ? "Too many folders to watch live (Linux inotify limit; raise fs.inotify.max_user_watches). Changes are picked up every few minutes."
            : $"Live watching stopped: {ex.Message}";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _batcher.Dispose();
        _signals.OnCompleted();
    }
}

/// <summary>
/// Collects changed paths and hands them over once events stop for <see cref="WatcherTiming.Quiet"/>,
/// or at the latest <see cref="WatcherTiming.MaxDelay"/> after the batch started.
/// </summary>
internal sealed class ChangeBatcher : IDisposable
{
    private readonly WatcherTiming _timing;
    private readonly Action<IReadOnlyCollection<string>> _flush;
    private readonly Lock _lock = new();
    private readonly Timer _quiet;
    private readonly Timer _deadline;
    private HashSet<string> _pending = new(StringComparer.Ordinal);
    private bool _deadlineArmed;
    private bool _disposed;

    public ChangeBatcher(WatcherTiming timing, Action<IReadOnlyCollection<string>> flush)
    {
        _timing = timing;
        _flush = flush;
        _quiet = new Timer(_ => Flush());
        _deadline = new Timer(_ => Flush());
    }

    public void Add(string path)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _pending.Add(path);
            _quiet.Change(_timing.Quiet, Timeout.InfiniteTimeSpan);
            if (!_deadlineArmed)
            {
                _deadline.Change(_timing.MaxDelay, Timeout.InfiniteTimeSpan);
                _deadlineArmed = true;
            }
        }
    }

    private void Flush()
    {
        HashSet<string> batch;
        lock (_lock)
        {
            if (_disposed || _pending.Count == 0)
                return;
            batch = _pending;
            _pending = new HashSet<string>(StringComparer.Ordinal);
            _quiet.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _deadline.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _deadlineArmed = false;
        }
        _flush(batch);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _quiet.Dispose();
            _deadline.Dispose();
        }
    }
}
