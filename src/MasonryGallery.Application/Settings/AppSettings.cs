namespace MasonryGallery.Application.Settings;

using System.IO.Enumeration;
using MasonryGallery.Application.Pipeline;
using MasonryGallery.Domain;

/// <summary>
/// User-adjustable options (design §10). Defaults come from appsettings.json next to the executable;
/// the user's changes are stored in settings.json in the data folder.
/// </summary>
public sealed record AppSettings
{
    public const int MinQuality = 50, MaxQuality = 95, MaxWorkers = 64, MaxSlideSeconds = 3600;

    /// <summary>Probe and thumbnail workers; null picks a count from the CPU and the disk type.</summary>
    public int? Workers { get; init; }

    /// <summary>Concurrent ffprobe/ffmpeg processes; null picks half the cores.</summary>
    public int? VideoWorkers { get; init; }

    /// <summary>WebP quality of new thumbnails. Existing thumbnails keep theirs.</summary>
    public int ThumbnailQuality { get; init; } = ThumbnailFormat.DefaultQuality;

    /// <summary>
    /// Size cap for lightbox previews, which are recreated on demand; 0 means no cap.
    /// Grid thumbnails are never evicted: every tile needs one.
    /// </summary>
    public int PreviewCacheLimitMb { get; init; } = 1024;

    /// <summary>
    /// File or folder names to skip, with * and ? wildcards (e.g. "*.part", "Screenshots").
    /// Matched against each name in a path, never the whole path.
    /// </summary>
    public IReadOnlyList<string> IgnorePatterns { get; init; } = [];

    /// <summary>How long presentation autoplay shows each image before moving on. Videos play to their end.</summary>
    public int SlideshowSeconds { get; init; } = 5;

    /// <summary>Folder holding ffmpeg and ffprobe; null searches the bundled copy, then PATH. Applies after restart.</summary>
    public string? FfmpegFolder { get; init; }

    public Result Validate()
    {
        if (Workers is < 1 or > MaxWorkers)
            return Result.Failure($"Workers must be between 1 and {MaxWorkers}.");
        if (VideoWorkers is < 1 or > MaxWorkers)
            return Result.Failure($"Video workers must be between 1 and {MaxWorkers}.");
        if (ThumbnailQuality is < MinQuality or > MaxQuality)
            return Result.Failure($"Thumbnail quality must be between {MinQuality} and {MaxQuality}.");
        if (SlideshowSeconds is < 1 or > MaxSlideSeconds)
            return Result.Failure($"Slideshow time must be between 1 and {MaxSlideSeconds} seconds.");
        if (PreviewCacheLimitMb < 0)
            return Result.Failure("The preview cache limit can't be negative.");
        foreach (var pattern in IgnorePatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return Result.Failure("Ignore patterns can't be blank.");
            if (pattern.IndexOfAny(['/', '\\']) >= 0)
                return Result.Failure($"\"{pattern}\" contains a slash; patterns match single file or folder names.");
        }
        if (FfmpegFolder is not null && !Path.IsPathFullyQualified(FfmpegFolder))
            return Result.Failure("The ffmpeg folder must be a full path.");
        return Result.Success();
    }

    /// <summary>True when <paramref name="name"/> (one file or folder name) matches an ignore pattern.</summary>
    public bool IsIgnored(ReadOnlySpan<char> name)
    {
        foreach (var pattern in IgnorePatterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true))
                return true;
        }
        return false;
    }

    // Records compare lists by reference; settings are compared by content to detect what a save changed.
    public bool Equals(AppSettings? other)
        => other is not null
           && Workers == other.Workers
           && VideoWorkers == other.VideoWorkers
           && ThumbnailQuality == other.ThumbnailQuality
           && PreviewCacheLimitMb == other.PreviewCacheLimitMb
           && SlideshowSeconds == other.SlideshowSeconds
           && IgnorePatterns.SequenceEqual(other.IgnorePatterns)
           && FfmpegFolder == other.FfmpegFolder;

    public override int GetHashCode()
        => HashCode.Combine(Workers, VideoWorkers, ThumbnailQuality, PreviewCacheLimitMb, SlideshowSeconds, IgnorePatterns.Count, FfmpegFolder);
}
