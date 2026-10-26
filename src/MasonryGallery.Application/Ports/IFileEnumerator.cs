namespace MasonryGallery.Application.Ports;

/// <param name="SidecarPath">The file's XMP sidecar ("a.jpg.xmp" or "a.xmp"), relative to the root, if any.</param>
public record FileEntry(string RelativePath, long Size, DateTime ModifiedUtc,
    string? SidecarPath = null, DateTime? SidecarModifiedUtc = null);

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

    /// <summary>
    /// The media files an XMP sidecar describes (relative to the root): "a.jpg" for "a.jpg.xmp",
    /// every "a.*" media file for "a.xmp". Empty when the path is not a sidecar or describes nothing.
    /// </summary>
    IReadOnlyList<string> FindSidecarOwners(string rootPath, string relSidecarPath);

    /// <summary>False when the library root is unplugged, unmounted or otherwise unreachable.</summary>
    bool RootExists(string rootPath);

    bool IsNetworkPath(string path);
    bool IsRotationalDisk(string path);
}
