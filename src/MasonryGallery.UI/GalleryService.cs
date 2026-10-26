namespace MasonryGallery.UI;

using System.Diagnostics;
using MasonryGallery.Application.Ports;
using MasonryGallery.Application.Pipeline;
using MasonryGallery.Application.Settings;
using MasonryGallery.Application.UseCases;
using MasonryGallery.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>Base URL and per-launch access token of the local media server.</summary>
public sealed record MediaEndpoint(string BaseUrl, string Token);

/// <summary>Where the app keeps its data and logs, for "open folder" actions.</summary>
public sealed record AppFolders(string Data, string Logs);

public sealed class GalleryService : IDisposable
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<GalleryService>? _logger;
    private readonly ViewportHint _viewport;
    private readonly List<IDisposable> _subscriptions = [];

    /// <summary>Ints per item in <see cref="GetLayoutAsync"/>'s result: id, w, h, kind, thumbReady, rgb, favorite.</summary>
    public const int LayoutStride = 7;

    public MediaEndpoint Endpoint { get; }

    private readonly ScanScheduler? _scheduler;
    private readonly LibraryMonitor? _monitor;

    public GalleryService(IServiceProvider sp,
        MediaEndpoint endpoint,
        ViewportHint viewport,
        ILogger<GalleryService>? logger = null,
        IProgressReporter? progressReporter = null,
        ScanScheduler? scheduler = null,
        LibraryMonitor? monitor = null)
    {
        _sp = sp;
        Endpoint = endpoint;
        _viewport = viewport;
        _logger = logger;
        _scheduler = scheduler;
        _monitor = monitor;
        if (progressReporter is not null)
        {
            _subscriptions.Add(progressReporter.Events.Subscribe(evt => ProgressChanged?.Invoke(this, evt)));
            _subscriptions.Add(progressReporter.Changes.Subscribe(change => GalleryChanged?.Invoke(this, change)));
        }
        if (scheduler is not null)
        {
            scheduler.Completed += OnScanCompleted;
            scheduler.BusyChanged += OnBusyChanged;
        }
        if (monitor is not null)
            monitor.StatusChanged += OnWatchStatusChanged;
    }

    // Handlers marshal to the UI thread, and Photino's dispatch blocks the caller until the UI thread has run it.
    // Forwarding from the thread pool keeps scan loops and the monitor from ever waiting on the window.
    // Handlers read current state (IsBusy, WatchStatuses), so reordering is harmless.
    private void OnScanCompleted(ScanCompleted done)
        => ThreadPool.QueueUserWorkItem(_ => ScanCompleted?.Invoke(this, done));
    private void OnBusyChanged()
        => ThreadPool.QueueUserWorkItem(_ => BusyChanged?.Invoke(this, EventArgs.Empty));
    private void OnWatchStatusChanged()
        => ThreadPool.QueueUserWorkItem(_ => WatchStatusChanged?.Invoke(this, EventArgs.Empty));

    /// <summary>A full or targeted scan finished (raised on a worker thread).</summary>
    public event EventHandler<ScanCompleted>? ScanCompleted;

    /// <summary><see cref="IsBusy"/> may have changed.</summary>
    public event EventHandler? BusyChanged;

    /// <summary><see cref="WatchStatuses"/> may have changed.</summary>
    public event EventHandler? WatchStatusChanged;

    /// <summary>True while any library is being scanned.</summary>
    public bool IsBusy => _scheduler?.IsBusy ?? false;

    /// <summary>How each enabled library is kept current: watched live, polled, or not at the moment.</summary>
    public IReadOnlyDictionary<LibraryId, WatchStatus> WatchStatuses
        => _monitor?.Statuses ?? new Dictionary<LibraryId, WatchStatus>();

    /// <summary>Status line updates from running scans.</summary>
    public event EventHandler<PipelineEvent>? ProgressChanged;

    /// <summary>Batched item and thumbnail changes, a few per second at most.</summary>
    public event EventHandler<GalleryChange>? GalleryChanged;

    private T Get<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    // ── Libraries ──
    public async Task<IReadOnlyList<Library>> GetLibrariesAsync(CancellationToken ct = default)
    {
        using var scope = _sp.CreateScope();
        return await Get<ILibraryRepository>(scope).ListAsync(ct);
    }

    public async Task<Result<Library>> AddLibraryAsync(string name, string rootPath, CancellationToken ct = default)
    {
        using var scope = _sp.CreateScope();
        return await Get<AddLibrary>(scope).ExecuteAsync(name, rootPath, ct);
    }

    /// <summary>Queues a full rescan; it runs after any scan already in progress for that library.</summary>
    public void RequestScan(LibraryId libraryId) => _scheduler?.RequestFullScan(libraryId);

    /// <summary>
    /// Call after adding, removing, enabling or disabling libraries: starts or stops their watchers
    /// and scans libraries that are new to the monitor.
    /// </summary>
    public Task LibrariesChangedAsync() => _monitor?.SyncAsync() ?? Task.CompletedTask;

    public async Task<Result> RemoveLibraryAsync(LibraryId libraryId, CancellationToken ct = default)
    {
        if (_scheduler?.IsScanning(libraryId) == true)
            return Result.Failure("Wait for the scan to finish before removing this library.");
        using var scope = _sp.CreateScope();
        return await Get<RemoveLibrary>(scope).ExecuteAsync(libraryId, ct);
    }

    /// <summary>Disabled libraries are hidden and not scanned. Following symlinks takes effect on the next scan.</summary>
    public async Task<Result> SetLibraryOptionsAsync(Library library, bool enabled, bool followSymlinks, CancellationToken ct = default)
    {
        using var scope = _sp.CreateScope();
        return await Get<ILibraryRepository>(scope).UpdateAsync(new Library(library.Name, library.RootPath)
        {
            Id = library.Id, Enabled = enabled, FollowSymlinks = followSymlinks, Status = library.Status,
            LastScanId = library.LastScanId, LastScanUtc = library.LastScanUtc
        }, ct);
    }

    // ── Gallery ──

    /// <summary>
    /// Flat [id, w, h, kind, thumbReady, rgb, favorite] * n, the cheap wire format layout.js consumes.
    /// rgb is the placeholder colour (0xRRGGBB) or -1.
    /// </summary>
    public async Task<int[]> GetLayoutAsync(MediaFilter filter, CancellationToken ct = default)
    {
        using var scope = _sp.CreateScope();
        var items = await Get<GetLayoutItems>(scope).ExecuteAsync(filter, ct);
        var flat = new int[items.Count * LayoutStride];
        var i = 0;
        foreach (var it in items)
        {
            flat[i++] = it.Id;
            flat[i++] = it.Width;
            flat[i++] = it.Height;
            flat[i++] = (int)it.Kind;
            flat[i++] = it.ThumbReady ? 1 : 0;
            flat[i++] = PlaceholderColor.FromBytes(it.Placeholder);
            flat[i++] = it.Favorite ? 1 : 0;
        }
        return flat;
    }

    /// <summary>File names aligned with <paramref name="ids"/> ("" when unknown); used as tile labels.</summary>
    public async Task<string[]> GetFileNamesAsync(int[] ids, CancellationToken ct = default)
    {
        using var scope = _sp.CreateScope();
        var names = await Get<IMediaRepository>(scope).GetFileNamesAsync(ids, ct);
        return Array.ConvertAll(ids, id => names.GetValueOrDefault(id, ""));
    }

    /// <summary>On-screen IDs still waiting for a thumbnail; the indexer serves them first.</summary>
    public void SetViewport(int[] ids) => _viewport.Set(ids);

    public async Task<MediaItem?> GetMediaAsync(int id, CancellationToken ct = default)
    {
        using var scope = _sp.CreateScope();
        return await Get<IMediaRepository>(scope).GetByIdAsync(id, ct);
    }

    // ── Favorites and tags ──
    public async Task SetFavoriteAsync(int mediaId, bool favorite, CancellationToken ct = default)
    {
        using var scope = _sp.CreateScope();
        await Get<IMediaRepository>(scope).SetFavoriteAsync(mediaId, favorite, ct);
    }

    /// <summary>All tags of an item: the user's own and those read from its sidecar (<see cref="Tag.FromSidecar"/>).</summary>
    public async Task<IReadOnlyList<Tag>> GetTagsAsync(int mediaId, CancellationToken ct = default)
    {
        using var scope = _sp.CreateScope();
        return await Get<IMediaRepository>(scope).GetTagsForMediaAsync(mediaId, ct);
    }

    /// <summary>Replaces the user's own tags; sidecar tags are unaffected.</summary>
    public async Task SetTagsAsync(int mediaId, IEnumerable<string> tags, CancellationToken ct = default)
    {
        using var scope = _sp.CreateScope();
        await Get<IMediaRepository>(scope).SetTagsAsync(mediaId, tags, ct);
    }

    public async Task<IReadOnlyList<TagCount>> ListTagsAsync(CancellationToken ct = default)
    {
        using var scope = _sp.CreateScope();
        return await Get<IMediaRepository>(scope).ListTagsAsync(ct);
    }

    /// <summary>Autocomplete: tags matching what was typed so far, most used first.</summary>
    public async Task<IReadOnlyList<TagCount>> SearchTagsAsync(string prefix, int limit = 12, CancellationToken ct = default)
    {
        using var scope = _sp.CreateScope();
        return await Get<IMediaRepository>(scope).SearchTagsAsync(prefix, limit, ct);
    }

    /// <summary>Creator, source link, title, description and rating from the item's XMP sidecar, if it has one.</summary>
    public async Task<SidecarData?> GetSidecarAsync(int mediaId, CancellationToken ct = default)
    {
        using var scope = _sp.CreateScope();
        return await Get<IMediaRepository>(scope).GetSidecarAsync(mediaId, ct);
    }

    // ── Problems ──
    public async Task<int> CountFailedAsync(CancellationToken ct = default)
    {
        using var scope = _sp.CreateScope();
        return await Get<IMediaRepository>(scope).CountFailedAsync(ct);
    }

    public async Task<IReadOnlyList<FailedItem>> GetFailedAsync(int limit = 200, CancellationToken ct = default)
    {
        using var scope = _sp.CreateScope();
        return await Get<IMediaRepository>(scope).GetFailedAsync(limit, ct);
    }

    // ── Settings and cache ──
    public AppSettings GetSettings() => _sp.GetRequiredService<ISettingsStore>().Current;

    public AppSettings GetDefaultSettings() => _sp.GetRequiredService<ISettingsStore>().Defaults;

    /// <summary>Validates and applies; workers and quality take effect from the next scan, ignore patterns rescan.</summary>
    public Task<Result> SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
        => _sp.GetRequiredService<ISettingsStore>().SaveAsync(settings, ct);

    public Task<(CacheUsage Thumbnails, CacheUsage Previews)> GetCacheUsageAsync(CancellationToken ct = default)
        => _sp.GetRequiredService<CacheJanitor>().GetUsageAsync(ct);

    public Task<CacheUsage> ClearPreviewsAsync(CancellationToken ct = default)
        => _sp.GetRequiredService<CacheJanitor>().ClearPreviewsAsync(ct);

    // ── Desktop integration ──

    public Result OpenLogsFolder() => OpenWithShell(_sp.GetRequiredService<AppFolders>().Logs);

    /// <summary>
    /// Opens a sidecar's source link in the browser. Sidecars are untrusted files, so only web links are
    /// handed to the shell: a file: or custom-scheme URL could start a program.
    /// </summary>
    public Result OpenLink(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? OpenWithShell(uri.AbsoluteUri)
            : Result.Failure("Only web links can be opened.");

    /// <summary>
    /// Opens the original in the system's default app (xdg-open on Linux), or its folder in the file manager.
    /// The fallback for videos the WebView can't decode.
    /// </summary>
    public async Task<Result> OpenExternallyAsync(int mediaId, bool showFolder = false, CancellationToken ct = default)
    {
        using var scope = _sp.CreateScope();
        var item = await Get<IMediaRepository>(scope).GetByIdAsync(mediaId, ct);
        if (item is null)
            return Result.Failure("This item no longer exists.");
        var library = await Get<ILibraryRepository>(scope).GetByIdAsync(item.LibraryId, ct);
        if (!library.IsSuccess)
            return Result.Failure(library.Error!);

        var root = Path.GetFullPath(library.Value!.RootPath);
        var path = Path.GetFullPath(Path.Combine(root, item.RelPath));
        // Same containment rule as the media server: never hand the shell a path outside the library.
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return Result.Failure("The file is outside its library.");

        var target = showFolder ? Path.GetDirectoryName(path)! : path;
        if (!Path.Exists(target))
            return Result.Failure("The file is not available. Is the drive connected?");
        return OpenWithShell(target);
    }

    private Result OpenWithShell(string target)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return Result.Success();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger?.LogWarning(ex, "Could not open {Path}", target);
            return Result.Failure("No application is set up to open this file.");
        }
    }

    // ── File URLs (Kestrel media server) ──
    public string GetThumbnailUrl(int mediaId, string size = "small") =>
        $"{Endpoint.BaseUrl}/thumb/{mediaId}/{size}?t={Endpoint.Token}";

    public string GetMediaUrl(int mediaId) =>
        $"{Endpoint.BaseUrl}/media/{mediaId}?t={Endpoint.Token}";

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        if (_scheduler is not null)
        {
            _scheduler.Completed -= OnScanCompleted;
            _scheduler.BusyChanged -= OnBusyChanged;
        }
        if (_monitor is not null)
            _monitor.StatusChanged -= OnWatchStatusChanged;
    }
}
