namespace MasonryGallery.Host;

/// <summary>
/// Where the app keeps its files (design §6, §9). The DB is user data; thumbnails are a cache that is safe to delete.
/// Linux: $XDG_DATA_HOME/MasonryGallery and $XDG_CACHE_HOME/MasonryGallery.
/// Windows: %APPDATA%\MasonryGallery and %LOCALAPPDATA%\MasonryGallery\cache.
/// </summary>
internal static class AppPaths
{
    private const string AppName = "MasonryGallery";

    // On Linux .NET maps LocalApplicationData to $XDG_DATA_HOME (default ~/.local/share).
    public static string Data { get; } = Path.Combine(
        Environment.GetFolderPath(OperatingSystem.IsWindows()
            ? Environment.SpecialFolder.ApplicationData
            : Environment.SpecialFolder.LocalApplicationData),
        AppName);

    public static string Cache { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, "cache")
        : Path.Combine(XdgCacheHome(), AppName);

    public static string Database => Path.Combine(Data, "gallery.db");
    public static string Thumbnails => Path.Combine(Cache, "thumbs");
    public static string Logs => Path.Combine(Data, "logs");

    // Earlier builds kept the DB and thumbnails together in ~/.config/MasonryGallery (%APPDATA% on Windows).
    private static string Legacy { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    private static string XdgCacheHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        return Path.IsPathFullyQualified(xdg ?? "")
            ? xdg!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(Logs);
    }

    /// <summary>One-time move from the legacy layout. Never overwrites; a failed thumbnail move just means regeneration.</summary>
    public static void MoveLegacyData(Serilog.ILogger log)
    {
        var legacyDb = Path.Combine(Legacy, "gallery.db");
        if (!File.Exists(Database) && File.Exists(legacyDb))
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(legacyDb + suffix))
                    File.Move(legacyDb + suffix, Database + suffix);
            }
            log.Information("Moved database from {From} to {To}", legacyDb, Database);
        }

        var legacyThumbs = Path.Combine(Legacy, "thumbs");
        if (Directory.Exists(legacyThumbs) && !Directory.Exists(Thumbnails))
        {
            try
            {
                Directory.Move(legacyThumbs, Thumbnails);
                log.Information("Moved thumbnails from {From} to {To}", legacyThumbs, Thumbnails);
            }
            catch (IOException ex)
            {
                // e.g. ~/.config and ~/.cache on different file systems.
                log.Warning(ex, "Could not move thumbnails from {From}; they will be regenerated", legacyThumbs);
            }
        }
    }
}
