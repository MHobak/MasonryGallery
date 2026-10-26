namespace MasonryGallery.Infrastructure.Video;

/// <summary>Finds ffmpeg and ffprobe (design §11). Returns null when a tool is nowhere to be found.</summary>
public static class FfmpegLocator
{
    public static string? Locate(string? configuredFolder = null) => Find("ffmpeg", configuredFolder);

    public static string? LocateProbe(string? configuredFolder = null) => Find("ffprobe", configuredFolder);

    // A folder the user chose wins over the copy bundled with the app, which wins over PATH.
    private static string? Find(string tool, string? configuredFolder)
    {
        var exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        foreach (var dir in new[] { configuredFolder, Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg") })
        {
            if (dir is not null && File.Exists(Path.Combine(dir, exe)))
                return Path.Combine(dir, exe);
        }
        return FindInPath(exe);
    }

    private static string? FindInPath(string exe)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.Combine(dir, exe);
            if (File.Exists(path))
                return path;
        }
        return null;
    }
}
