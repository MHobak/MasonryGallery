using System.Diagnostics;
using Dapper;
using MasonryGallery.Application.Pipeline;
using MasonryGallery.Application.Ports;
using MasonryGallery.Application.UseCases;
using MasonryGallery.Host;
using MasonryGallery.Infrastructure.FileSystem;
using MasonryGallery.Infrastructure.Imaging;
using MasonryGallery.Infrastructure.Metadata;
using MasonryGallery.Infrastructure.Persistence;
using MasonryGallery.Infrastructure.Settings;
using MasonryGallery.Infrastructure.Thumbnails;
using MasonryGallery.Infrastructure.Video;
using MasonryGallery.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Photino.Blazor;
using Serilog;
using Serilog.Extensions.Logging;

var thumbVersionDir = $"v{ThumbnailFormat.Version}";

AppPaths.EnsureCreated();
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    // Kestrel logs every request URL at Information; those URLs carry the media server token.
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    // Shared: a main window and its viewer windows write the same log.
    .WriteTo.File(Path.Combine(AppPaths.Logs, "gallery-.txt"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7, shared: true)
    .CreateLogger();

// The first process owns the index: it migrates, scans, watches and cleans up. Two of those at once would corrupt it,
// so later launches open viewer windows that browse the same index without indexing anything (see AppMode).
using var instanceLock = SingleInstanceLock.TryAcquire(AppPaths.InstanceLock);
var isViewer = instanceLock is null;
if (isViewer)
    Log.Information("Another MasonryGallery owns the index; opening a viewer window");

MediaServer? mediaServer = null;
LibraryMonitor? monitor = null;
ScanScheduler? scheduler = null;
CacheJanitor? janitor = null;
DatabaseChangePoller? changes = null;
try
{
    if (!isViewer)
        AppPaths.MoveLegacyData(Log.Logger);
    var thumbRoot = AppPaths.Thumbnails;

    using var loggerFactory = new SerilogLoggerFactory(Log.Logger);
    var settings = new JsonSettingsStore(AppPaths.DefaultSettings, AppPaths.UserSettings, loggerFactory.CreateLogger<JsonSettingsStore>());

    var ffmpegPath = FfmpegLocator.Locate(settings.Current.FfmpegFolder) ?? "ffmpeg";
    var ffprobePath = FfmpegLocator.LocateProbe(settings.Current.FfmpegFolder) ?? "ffprobe";
    var connFactory = new SqliteConnectionFactory(AppPaths.Database, loggerFactory.CreateLogger<SqliteConnectionFactory>());

    // ── Schema migration (before anything reads the DB); a viewer waits for the main window's ──
    if (!isViewer)
    {
        await SchemaMigrator.MigrateAsync(connFactory, loggerFactory.CreateLogger("SchemaMigrator"));
    }
    else if (await WaitForSchemaAsync(connFactory) is { } problem)
    {
        Log.Error("Cannot open a viewer window: {Problem}", problem);
        Console.Error.WriteLine($"MasonryGallery: {problem}");
        return 1;
    }

    // The cache is safe to delete; when it is gone, queue every thumbnail again instead of serving 404s.
    if (!isViewer && !Directory.Exists(Path.Combine(thumbRoot, thumbVersionDir)))
    {
        using var conn = connFactory.CreateConnection();
        await conn.ExecuteAsync("UPDATE Media SET ThumbState = 0 WHERE ThumbState != 0");
    }
    var thumbStore = new ShardedThumbnailStore(thumbRoot, thumbVersionDir, loggerFactory.CreateLogger<ShardedThumbnailStore>());
    // Older versions' caches can be gigabytes; reclaim them without delaying startup.
    if (!isViewer)
        _ = Task.Run(() => thumbStore.DeleteStaleVersionsAsync());

    var thumbnailGenerator = new CompositeThumbnailGenerator(
        new SkiaThumbnailGenerator(loggerFactory.CreateLogger<SkiaThumbnailGenerator>()),
        new FfmpegThumbnailGenerator(ffmpegPath, loggerFactory.CreateLogger<FfmpegThumbnailGenerator>()));

    // ── Media HTTP server (thumb + media file serving) ──
    mediaServer = await MediaServer.StartAsync(connFactory, thumbStore, thumbnailGenerator, settings);
    Log.Information("Media server listening on {Url}", mediaServer.BaseUrl);

    // ── Photino Blazor app ──
    var appBuilder = PhotinoBlazorAppBuilder.CreateDefault(args);

    appBuilder.Services
        .AddLogging()
        .AddSerilog()
        // Infrastructure
        .AddSingleton(connFactory)
        .AddSingleton<ISettingsStore>(settings)
        .AddSingleton<IMediaRepository, SqliteMediaRepository>()
        .AddSingleton<ILibraryRepository, SqliteLibraryRepository>()
        .AddSingleton<IFileEnumerator, FileSystemEnumerator>()
        .AddSingleton<IMediaProbe>(sp => new CompositeProbe(
            new SkiaProbe(sp.GetRequiredService<ILogger<SkiaProbe>>()),
            new FfprobeProbe(ffprobePath, sp.GetRequiredService<ILogger<FfprobeProbe>>())))
        .AddSingleton<IThumbnailGenerator>(thumbnailGenerator)
        .AddSingleton<ISidecarReader, XmpSidecarReader>()
        .AddSingleton<IThumbnailStore>(thumbStore)
        .AddSingleton<IClock, SystemClock>()
        // Use cases
        .AddSingleton<QueryMedia>()
        .AddSingleton<GetLayoutItems>()
        // UI
        .AddSingleton(new MediaEndpoint(mediaServer.BaseUrl, mediaServer.Token))
        .AddSingleton(new AppFolders(AppPaths.Data, AppPaths.Logs))
        .AddSingleton<IProgressReporter, PipelineProgressReporter>()
        .AddSingleton<ViewportHint>()
        .AddSingleton<FolderPicker>()
        .AddSingleton<WindowControl>()
        .AddSingleton(new AppMode(isViewer))
        .AddSingleton<GalleryService>();
    // Indexing and library management exist only in the main window; GalleryService takes them as optional.
    if (!isViewer)
    {
        appBuilder.Services
            .AddSingleton<IFileWatcherFactory, FileWatcherFactory>()
            .AddSingleton<AddLibrary>()
            .AddSingleton<RemoveLibrary>()
            .AddSingleton<Scan>()
            .AddSingleton<ILibraryScanner>(sp => sp.GetRequiredService<Scan>())
            .AddSingleton<ScanScheduler>()
            .AddSingleton<LibraryMonitor>()
            .AddSingleton<CacheJanitor>()
            .AddSingleton<IndexingPause>();
    }

    appBuilder.RootComponents.Add<App>("#app");

    var app = appBuilder.Build();

    app.Services.GetRequiredService<FolderPicker>().Pick =
        () => app.MainWindow.ShowOpenFolder("Choose a library folder")?.FirstOrDefault();
    var window = app.Services.GetRequiredService<WindowControl>();
    window.SetFullScreen = on => app.MainWindow.FullScreen = on;
    window.OpenNewWindow = LaunchViewer;

    app.MainWindow
        // Off: from level 1 up, Photino prints every message sent to the web view, including the whole
        // gallery layout (~18 MB at 578k items) on each reload; a terminal taking that in stalls the app.
        .SetLogVerbosity(0)
        .SetSize(1400, 900)
        .SetTitle(isViewer ? "MasonryGallery (viewer)" : "MasonryGallery");
    // A viewer opened on top of the main window, at the same spot, would look like nothing happened.
    if (isViewer)
        app.MainWindow.SetUseOsDefaultLocation(true);
    else
        app.MainWindow.Center();

    // Kestrel handles SIGTERM / Ctrl+C; take the window down with it rather than leave it without a media server.
    // A signal right after launch arrives before the native window exists, and Close() then throws; in that case
    // the created handler closes it instead. `closed` makes sure only one of the two actually closes it.
    var stopRequested = false;
    var closed = 0;
    void CloseWindow()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0)
            return;
        try { app.MainWindow.Close(); }
        catch (ApplicationException) { Volatile.Write(ref closed, 0); } // not created yet
    }
    app.MainWindow.RegisterWindowCreatedHandler((_, _) =>
    {
        if (Volatile.Read(ref stopRequested))
            CloseWindow();
    });
    mediaServer.Stopping.Register(() =>
    {
        Volatile.Write(ref stopRequested, true);
        CloseWindow();
    });

    // GalleryService subscribes to scan events; create it before the first scan can report.
    app.Services.GetRequiredService<GalleryService>();
    if (isViewer)
    {
        // The main window's scans reach this window only through the database: refresh the gallery when it changes.
        var reporter = app.Services.GetRequiredService<IProgressReporter>();
        changes = new DatabaseChangePoller(AppPaths.Database, reporter.ItemsChanged, TimeSpan.FromSeconds(2),
            loggerFactory.CreateLogger<DatabaseChangePoller>());
        changes.Start();
    }
    else
    {
        scheduler = app.Services.GetRequiredService<ScanScheduler>();
        monitor = app.Services.GetRequiredService<LibraryMonitor>();
        janitor = app.Services.GetRequiredService<CacheJanitor>();
        janitor.Start();
        // Startup reconcile of every library, then live watching and periodic rescans (design §5.4).
        // Not awaited: GTK needs app.Run() on the thread that built the window, and an await here could resume elsewhere.
        // Task.Run: setting up the watchers walks every library's folder tree (Linux watches each folder), which took
        // ~23 s on large drives; on this thread it ran before app.Run() and kept the window from opening.
        _ = Task.Run(() => monitor.StartAsync()).ContinueWith(
            t => Log.Error(t.Exception, "Library monitor failed to start"), TaskContinuationOptions.OnlyOnFaulted);
    }

    AppDomain.CurrentDomain.UnhandledException += (sender, error) =>
        app.MainWindow.ShowMessage("Fatal exception", error.ExceptionObject.ToString());

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application startup failed");
    return 1;
}
finally
{
    // Stop watchers first, then cancel scans; both leave the index consistent.
    changes?.Dispose();
    janitor?.Dispose();
    monitor?.Dispose();
    scheduler?.Dispose();
    if (mediaServer is not null)
        await mediaServer.DisposeAsync();
    Log.CloseAndFlush();
}
return 0;

// The main window may be mid-migration when a viewer starts; a viewer never migrates, it waits.
// Returns null when the index is ready, else why the viewer can't open.
static async Task<string?> WaitForSchemaAsync(SqliteConnectionFactory db)
{
    for (var attempt = 0; attempt < 120; attempt++)
    {
        var version = await SchemaMigrator.GetVersionAsync(db);
        if (version == SchemaMigrator.LatestVersion)
            return null;
        if (version > SchemaMigrator.LatestVersion)
            return "the running MasonryGallery is a newer version; open new windows from it instead.";
        await Task.Delay(500);
    }
    return "the main window hasn't finished preparing the index.";
}

// Another process of this same program; it finds the lock taken and comes up as a viewer.
static void LaunchViewer()
{
    var exe = Environment.ProcessPath!;
    var start = new ProcessStartInfo(exe) { UseShellExecute = false };
    // Started as "dotnet MasonryGallery.Host.dll" rather than through the native launcher.
    if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
    try
    {
        using var _ = Process.Start(start);
    }
    catch (System.ComponentModel.Win32Exception ex)
    {
        Log.Warning(ex, "Could not open a new window");
    }
}
