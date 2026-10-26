namespace MasonryGallery.Infrastructure.FileSystem;

using System.Collections.Frozen;
using System.IO.Enumeration;
using MasonryGallery.Application.Ports;
using MasonryGallery.Application.Settings;
using Microsoft.Extensions.Logging;

public sealed class FileSystemEnumerator : IFileEnumerator
{
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Extensions =
        new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".avif",
                ".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase)
            .GetAlternateLookup<ReadOnlySpan<char>>();

    // NAS index folders, recycle bins and dependency trees. Dot-folders are already skipped as hidden.
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> IgnoredDirs =
        new[] { "@eaDir", "#recycle", "#snapshot", "$RECYCLE.BIN", "System Volume Information", "node_modules" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase)
            .GetAlternateLookup<ReadOnlySpan<char>>();

    private readonly ILogger<FileSystemEnumerator> _logger;
    private readonly ISettingsStore? _settings;

    /// <param name="settings">Source of the user's ignore patterns, read at the start of each walk.</param>
    public FileSystemEnumerator(ILogger<FileSystemEnumerator> logger, ISettingsStore? settings = null)
    {
        _logger = logger;
        _settings = settings;
    }

    private AppSettings Settings => _settings?.Current ?? new AppSettings();

    private const string SidecarExtension = ".xmp";

    private static bool IsSidecar(ReadOnlySpan<char> name) => name.EndsWith(SidecarExtension, StringComparison.OrdinalIgnoreCase);

    // Sidecar names are matched without case: tools disagree on "a.JPG.xmp" vs "a.jpg.xmp".
    private static readonly StringComparer SidecarNames = StringComparer.OrdinalIgnoreCase;

    private readonly record struct RawEntry(string RelPath, long Length, DateTime ModifiedUtc, bool IsSidecar);

    public IAsyncEnumerable<FileEntry> EnumerateAsync(string rootPath, bool followSymlinks, CancellationToken ct = default)
        => EnumerateFromAsync(rootPath, "", followSymlinks, ct);

    public async IAsyncEnumerable<FileEntry> EnumerateSubtreeAsync(
        string rootPath,
        string relDirectory,
        bool followSymlinks,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // The walk below only applies its rules to folders it descends into; the starting folder and
        // its ancestors (hidden? under .nomedia? a link?) are checked here.
        if (Inspect(rootPath, relDirectory, followSymlinks).Kind != PathKind.Directory)
            yield break;
        await foreach (var entry in EnumerateFromAsync(rootPath, relDirectory, followSymlinks, ct))
            yield return entry;
    }

    private async IAsyncEnumerable<FileEntry> EnumerateFromAsync(
        string rootPath,
        string relStart,
        bool followSymlinks,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(rootPath))
        {
            _logger.LogWarning("Path does not exist: {Path}", rootPath);
            yield break;
        }

        // The enumeration below is blocking I/O; get off the caller's thread first.
        await Task.Yield();

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var start = Path.Join(root, relStart);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
                               | (followSymlinks ? 0 : FileAttributes.ReparsePoint),
        };
        var followed = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var settings = Settings;

        // Unlike the old per-folder try/catch, I/O errors here propagate: the scan then fails without
        // sweeping, instead of treating an unreadable folder's files as deleted.
        var entries = new FileSystemEnumerable<RawEntry>(start,
            (ref FileSystemEntry e) => new RawEntry(
                Path.GetRelativePath(root, e.ToFullPath()), e.Length, e.LastWriteTimeUtc.UtcDateTime, IsSidecar(e.FileName)),
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
                !e.IsDirectory
                && (Extensions.Contains(Path.GetExtension(e.FileName)) || IsSidecar(e.FileName))
                && !settings.IsIgnored(e.FileName),
            ShouldRecursePredicate = (ref FileSystemEntry e) =>
                !IgnoredDirs.Contains(e.FileName)
                && !settings.IsIgnored(e.FileName)
                && !File.Exists(Path.Join(e.ToFullPath(), ".nomedia"))
                && ((e.Attributes & FileAttributes.ReparsePoint) == 0 || ShouldFollow(e.ToFullPath(), followed)),
        };

        // .NET lists a folder completely before moving on to the next one, so a file and its sidecar
        // arrive in the same run of entries: pair them per folder, at no extra disk cost.
        string? folder = null;
        var media = new List<RawEntry>();
        var sidecars = new Dictionary<string, RawEntry>(SidecarNames);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var entryFolder = Path.GetDirectoryName(entry.RelPath);
            if (entryFolder != folder)
            {
                foreach (var file in Pair(media, sidecars))
                    yield return file;
                media.Clear();
                sidecars.Clear();
                folder = entryFolder;
            }
            if (entry.IsSidecar)
                sidecars[entry.RelPath[..^SidecarExtension.Length]] = entry;
            else
                media.Add(entry);
        }
        foreach (var file in Pair(media, sidecars))
            yield return file;
    }

    // "a.jpg.xmp" (darktable, gallery-dl, digiKam) is preferred over "a.xmp" (Lightroom, Bridge).
    private static IEnumerable<FileEntry> Pair(List<RawEntry> media, Dictionary<string, RawEntry> sidecars)
    {
        foreach (var m in media)
        {
            var found = sidecars.TryGetValue(m.RelPath, out var sidecar)
                        || sidecars.TryGetValue(Path.ChangeExtension(m.RelPath, null), out sidecar);
            yield return found
                ? new FileEntry(m.RelPath, m.Length, m.ModifiedUtc, sidecar.RelPath, sidecar.ModifiedUtc)
                : new FileEntry(m.RelPath, m.Length, m.ModifiedUtc);
        }
    }

    // Loop protection for followed directory links: skip a link into one of its own ancestors,
    // and visit each target once even when several links point at it.
    private static bool ShouldFollow(string linkPath, HashSet<string> followed)
    {
        var target = Directory.ResolveLinkTarget(linkPath, returnFinalTarget: true)?.FullName;
        if (target is null)
            return true;
        target = Path.TrimEndingDirectorySeparator(target);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var isAncestor = (linkPath + Path.DirectorySeparatorChar).StartsWith(target + Path.DirectorySeparatorChar, comparison);
        return !isAncestor && followed.Add(target);
    }

    public PathInfo Inspect(string rootPath, string relPath, bool followSymlinks)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var segments = relPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var full = Path.Join([root, .. segments]);
        try
        {
            var isDirectory = Directory.Exists(full);
            if (!isDirectory && !File.Exists(full))
                return new PathInfo(PathKind.Missing);

            // The rules EnumerateAsync applies while descending, applied to each folder on the way to the item.
            var settings = Settings;
            var current = root;
            for (var i = 0; i < segments.Length; i++)
            {
                current = Path.Join(current, segments[i]);
                var isFolder = i < segments.Length - 1 || isDirectory;
                if (segments[i].StartsWith('.')
                    || (File.GetAttributes(current) & (FileAttributes.Hidden | FileAttributes.System)) != 0
                    || settings.IsIgnored(segments[i])
                    || (!followSymlinks && new FileInfo(current).LinkTarget is not null)
                    || (isFolder && (IgnoredDirs.Contains(segments[i]) || File.Exists(Path.Join(current, ".nomedia")))))
                    return new PathInfo(PathKind.Ignored);
            }

            if (isDirectory)
                return new PathInfo(PathKind.Directory);
            if (!Extensions.Contains(Path.GetExtension(full.AsSpan())))
                return new PathInfo(PathKind.Ignored);

            var file = new FileInfo(full);
            var sidecar = new[] { full + SidecarExtension, Path.ChangeExtension(full, SidecarExtension) }
                .Select(p => new FileInfo(p))
                .FirstOrDefault(f => f.Exists && !settings.IsIgnored(f.Name));
            return new PathInfo(PathKind.File, new FileEntry(Path.GetRelativePath(root, full), file.Length, file.LastWriteTimeUtc,
                sidecar is null ? null : Path.GetRelativePath(root, sidecar.FullName), sidecar?.LastWriteTimeUtc));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new PathInfo(PathKind.Missing); // deleted while we looked
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(ex, "Cannot inspect {Path}", full);
            return new PathInfo(PathKind.Ignored);
        }
    }

    public IReadOnlyList<string> FindSidecarOwners(string rootPath, string relSidecarPath)
    {
        if (!IsSidecar(relSidecarPath))
            return [];
        var stem = relSidecarPath[..^SidecarExtension.Length];
        var full = Path.Join(rootPath, stem);
        try
        {
            if (Extensions.Contains(Path.GetExtension(stem.AsSpan())))
                return File.Exists(full) ? [stem.Replace('\\', '/')] : [];

            // "a.xmp" describes whichever "a.<ext>" media files sit next to it.
            var dir = Path.GetDirectoryName(full)!;
            if (!Directory.Exists(dir))
                return [];
            return Directory.EnumerateFiles(dir, Path.GetFileName(stem) + ".*")
                .Where(f => Extensions.Contains(Path.GetExtension(f.AsSpan())))
                .Select(f => Path.GetRelativePath(rootPath, f).Replace('\\', '/'))
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(ex, "Cannot look for the media of sidecar {Path}", full);
            return [];
        }
    }

    public bool RootExists(string rootPath) => Directory.Exists(rootPath);

    public bool IsNetworkPath(string path) => VolumeInfo.IsNetwork(path);

    public bool IsRotationalDisk(string path) => VolumeInfo.IsRotational(path);
}
