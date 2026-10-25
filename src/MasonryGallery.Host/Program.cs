using Dapper;
using MasonryGallery.Application.Pipeline;
using MasonryGallery.Application.Ports;
using MasonryGallery.Application.UseCases;
using MasonryGallery.Host;
using MasonryGallery.Infrastructure.FileSystem;
using MasonryGallery.Infrastructure.Imaging;
using MasonryGallery.Infrastructure.Persistence;
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
    .WriteTo.File(Path.Combine(AppPaths.Logs, "gallery-.txt"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
    .CreateLogger();

MediaServer? mediaServer = null;
LibraryMonitor? monitor = null;
ScanScheduler? scheduler = null;
try
{
    AppPaths.MoveLegacyData(Log.Logger);
    var thumbRoot = AppPaths.Thumbnails;

    var ffmpegPath = FfmpegLocator.Locate() ?? "ffmpeg";
    var ffprobePath = FfmpegLocator.LocateProbe() ?? "ffprobe";

    using var loggerFactory = new SerilogLoggerFactory(Log.Logger);
    var connFactory = new SqliteConnectionFactory(AppPaths.Database, loggerFactory.CreateLogger<SqliteConnectionFactory>());

    // ── Schema migration (before anything reads the DB) ──
    await SchemaMigrator.MigrateAsync(connFactory, loggerFactory.CreateLogger("SchemaMigrator"));

    // The cache is safe to delete; when it is gone, queue every thumbnail again instead of serving 404s.
    if (!Directory.Exists(Path.Combine(thumbRoot, thumbVersionDir)))
    {
        using var conn = connFactory.CreateConnection();
        await conn.ExecuteAsync("UPDATE Media SET ThumbState = 0 WHERE ThumbState != 0");
    }
    var thumbStore = new ShardedThumbnailStore(thumbRoot, thumbVersionDir, loggerFactory.CreateLogger<ShardedThumbnailStore>());
    // Older versions' caches can be gigabytes; reclaim them without delaying startup.
    _ = Task.Run(() => thumbStore.DeleteStaleVersionsAsync());

    var thumbnailGenerator = new CompositeThumbnailGenerator(
        new SkiaThumbnailGenerator(loggerFactory.CreateLogger<SkiaThumbnailGenerator>()),
        new FfmpegThumbnailGenerator(ffmpegPath, loggerFactory.CreateLogger<FfmpegThumbnailGenerator>()));

    // ── Media HTTP server (thumb + media file serving) ──
    mediaServer = await MediaServer.StartAsync(connFactory, thumbStore, thumbnailGenerator);
    Log.Information("Media server listening on {Url}", mediaServer.BaseUrl);

    // ── Photino Blazor app ──
    var appBuilder = PhotinoBlazorAppBuilder.CreateDefault(args);

    appBuilder.Services
        .AddLogging()
        .AddSerilog()
        // Infrastructure
        .AddSingleton(connFactory)
        .AddSingleton<IMediaRepository, SqliteMediaRepository>()
        .AddSingleton<ILibraryRepository, SqliteLibraryRepository>()
        .AddSingleton<IFileEnumerator, FileSystemEnumerator>()
        .AddSingleton<IMediaProbe>(sp => new CompositeProbe(
            new SkiaProbe(sp.GetRequiredService<ILogger<SkiaProbe>>()),
            new FfprobeProbe(ffprobePath, sp.GetRequiredService<ILogger<FfprobeProbe>>())))
        .AddSingleton<IThumbnailGenerator>(thumbnailGenerator)
        .AddSingleton<IThumbnailStore>(thumbStore)
        .AddSingleton<IFileWatcherFactory, FileWatcherFactory>()
        .AddSingleton<IClock, SystemClock>()
        // Use cases
        .AddSingleton<AddLibrary>()
        .AddSingleton<RemoveLibrary>()
        .AddSingleton<Scan>()
        .AddSingleton<ILibraryScanner>(sp => sp.GetRequiredService<Scan>())
        .AddSingleton<ScanScheduler>()
        .AddSingleton<LibraryMonitor>()
        .AddSingleton<QueryMedia>()
        .AddSingleton<GetLayoutItems>()
        // UI
        .AddSingleton(new MediaEndpoint(mediaServer.BaseUrl, mediaServer.Token))
        .AddSingleton<IProgressReporter, PipelineProgressReporter>()
        .AddSingleton<ViewportHint>()
        .AddSingleton<FolderPicker>()
        .AddSingleton<GalleryService>();

    appBuilder.RootComponents.Add<App>("#app");

    var app = appBuilder.Build();

    app.Services.GetRequiredService<FolderPicker>().Pick =
        () => app.MainWindow.ShowOpenFolder("Choose a library folder")?.FirstOrDefault();

    app.MainWindow
        .SetSize(1400, 900)
        .Center()
        .SetTitle("MasonryGallery");

    // Kestrel handles SIGTERM / Ctrl+C; take the window down with it rather than leave it without a media server.
    mediaServer.Stopping.Register(() => app.MainWindow.Close());

    // GalleryService subscribes to scan events; create it before the first scan can report.
    app.Services.GetRequiredService<GalleryService>();
    scheduler = app.Services.GetRequiredService<ScanScheduler>();
    monitor = app.Services.GetRequiredService<LibraryMonitor>();
    // Startup reconcile of every library, then live watching and periodic rescans (design §5.4).
    // Not awaited: GTK needs app.Run() on the thread that built the window, and an await here could resume elsewhere.
    _ = monitor.StartAsync().ContinueWith(
        t => Log.Error(t.Exception, "Library monitor failed to start"), TaskContinuationOptions.OnlyOnFaulted);

    AppDomain.CurrentDomain.UnhandledException += (sender, error) =>
        app.MainWindow.ShowMessage("Fatal exception", error.ExceptionObject.ToString());

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application startup failed");
}
finally
{
    // Stop watchers first, then cancel scans; both leave the index consistent.
    monitor?.Dispose();
    scheduler?.Dispose();
    if (mediaServer is not null)
        await mediaServer.DisposeAsync();
    Log.CloseAndFlush();
}
