namespace MasonryGallery.Infrastructure.FileSystem;

using System.Collections.Frozen;
using System.IO.Enumeration;
using MasonryGallery.Application.Ports;
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

    public FileSystemEnumerator(ILogger<FileSystemEnumerator> logger)
        => _logger = logger;

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

        // Unlike the old per-folder try/catch, I/O errors here propagate: the scan then fails without
        // sweeping, instead of treating an unreadable folder's files as deleted.
        var entries = new FileSystemEnumerable<FileEntry>(start,
            (ref FileSystemEntry e) => new FileEntry(
                Path.GetRelativePath(root, e.ToFullPath()), e.Length, e.LastWriteTimeUtc.UtcDateTime),
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
                !e.IsDirectory && Extensions.Contains(Path.GetExtension(e.FileName)),
            ShouldRecursePredicate = (ref FileSystemEntry e) =>
                !IgnoredDirs.Contains(e.FileName)
                && !File.Exists(Path.Join(e.ToFullPath(), ".nomedia"))
                && ((e.Attributes & FileAttributes.ReparsePoint) == 0 || ShouldFollow(e.ToFullPath(), followed)),
        };

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            yield return entry;
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
            var current = root;
            for (var i = 0; i < segments.Length; i++)
            {
                current = Path.Join(current, segments[i]);
                var isFolder = i < segments.Length - 1 || isDirectory;
                if (segments[i].StartsWith('.')
                    || (File.GetAttributes(current) & (FileAttributes.Hidden | FileAttributes.System)) != 0
                    || (!followSymlinks && new FileInfo(current).LinkTarget is not null)
                    || (isFolder && (IgnoredDirs.Contains(segments[i]) || File.Exists(Path.Join(current, ".nomedia")))))
                    return new PathInfo(PathKind.Ignored);
            }

            if (isDirectory)
                return new PathInfo(PathKind.Directory);
            if (!Extensions.Contains(Path.GetExtension(full.AsSpan())))
                return new PathInfo(PathKind.Ignored);

            var file = new FileInfo(full);
            return new PathInfo(PathKind.File, new FileEntry(Path.GetRelativePath(root, full), file.Length, file.LastWriteTimeUtc));
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

    public bool RootExists(string rootPath) => Directory.Exists(rootPath);

    public bool IsNetworkPath(string path) => VolumeInfo.IsNetwork(path);

    public bool IsRotationalDisk(string path) => VolumeInfo.IsRotational(path);
}
