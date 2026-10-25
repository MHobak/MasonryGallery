namespace MasonryGallery.Application.Ports;

public record FileEntry(string RelativePath, long Size, DateTime ModifiedUtc);

public enum PathKind
{
    /// <summary>Nothing there (deleted, or renamed away).</summary>
    Missing,
    /// <summary>Exists but would not be indexed: hidden, ignored folder, under .nomedia, unfollowed link, not media.</summary>
    Ignored,
    Directory,
    File
}

/// <param name="File">Set when <paramref name="Kind"/> is <see cref="PathKind.File"/>.</param>
public record PathInfo(PathKind Kind, FileEntry? File = null);

public interface IFileEnumerator
{
    /// <summary>
    /// Media files under <paramref name="rootPath"/>. Skips hidden entries, ignored folder names and
    /// folders containing a <c>.nomedia</c> file. Symlinks are skipped unless <paramref name="followSymlinks"/>.
    /// </summary>
    IAsyncEnumerable<FileEntry> EnumerateAsync(
        string rootPath,
        bool followSymlinks,
        CancellationToken ct = default);

    /// <summary>
    /// Media files under <paramref name="relDirectory"/> (relative to the root), with paths relative to the root.
    /// Same rules as <see cref="EnumerateAsync"/>.
    /// </summary>
    IAsyncEnumerable<FileEntry> EnumerateSubtreeAsync(
        string rootPath,
        string relDirectory,
        bool followSymlinks,
        CancellationToken ct = default);

    /// <summary>Classifies one path under the root by the same rules <see cref="EnumerateAsync"/> applies.</summary>
    PathInfo Inspect(string rootPath, string relPath, bool followSymlinks);

    /// <summary>False when the library root is unplugged, unmounted or otherwise unreachable.</summary>
    bool RootExists(string rootPath);

    bool IsNetworkPath(string path);
    bool IsRotationalDisk(string path);
}
