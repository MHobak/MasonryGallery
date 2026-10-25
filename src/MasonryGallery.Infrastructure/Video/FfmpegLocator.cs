namespace MasonryGallery.Infrastructure.Video;

using Microsoft.Extensions.Logging;

public static class FfmpegLocator
{
    public static string? Locate()
    {
        // 1. Check bundled copy
        var bundledDir = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg");
        if (OperatingSystem.IsWindows())
        {
            var bundled = Path.Combine(bundledDir, "ffmpeg.exe");
            if (File.Exists(bundled))
                return bundled;
        }
        else
        {
            var bundled = Path.Combine(bundledDir, "ffmpeg");
            if (File.Exists(bundled))
                return bundled;
        }

        // 2. Check configured path (from settings) - would be injected via options
        // For now, skip

        // 3. Check PATH
        return FindInPath("ffmpeg");
    }

    public static string? LocateProbe()
    {
        var bundledDir = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg");
        if (OperatingSystem.IsWindows())
        {
            var bundled = Path.Combine(bundledDir, "ffprobe.exe");
            if (File.Exists(bundled))
                return bundled;
        }
        else
        {
            var bundled = Path.Combine(bundledDir, "ffprobe");
            if (File.Exists(bundled))
                return bundled;
        }

        return FindInPath("ffprobe");
    }

    private static string? FindInPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.Combine(dir, name);
            if (OperatingSystem.IsWindows())
                path += ".exe";
            if (File.Exists(path))
                return path;
        }
        return null;
    }
}
