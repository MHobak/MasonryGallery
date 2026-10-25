namespace MasonryGallery.Application.Ports;

using MasonryGallery.Domain;

/// <summary>What a library watcher reports. The watcher is a hint; periodic full rescans stay the source of truth.</summary>
public abstract record WatchSignal
{
    /// <summary>Paths (relative to the root, '/'-separated) that changed during a quiet window. Files or folders.</summary>
    public sealed record Changed(IReadOnlyCollection<string> Paths) : WatchSignal;

    /// <summary>Events were dropped (buffer overflow); only a full rescan is reliable now.</summary>
    public sealed record Overflow : WatchSignal;

    /// <summary>The watcher stopped working (e.g. the inotify watch limit); the library must be polled instead.</summary>
    public sealed record Failed(string Reason) : WatchSignal;
}

/// <summary>Watches one library root recursively until disposed.</summary>
public interface IFileWatcher : IDisposable
{
    IObservable<WatchSignal> Signals { get; }
}

public interface IFileWatcherFactory
{
    /// <summary>Starts watching <paramref name="rootPath"/>, or says why live watching isn't possible there.</summary>
    Result<IFileWatcher> TryWatch(string rootPath);
}
