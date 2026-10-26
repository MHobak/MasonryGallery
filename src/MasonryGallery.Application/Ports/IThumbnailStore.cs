namespace MasonryGallery.Application.Ports;

public sealed record CacheUsage(int Files, long Bytes);

public interface IThumbnailStore
{
    Task<ReadOnlyMemory<byte>?> GetAsync(int mediaId, int size, CancellationToken ct = default);
    Task SaveAsync(int mediaId, int size, ReadOnlyMemory<byte> data, CancellationToken ct = default);
    Task DeleteAsync(int mediaId, CancellationToken ct = default);

    /// <summary>
    /// Deletes thumbnails whose media ID is not in <paramref name="knownIds"/>, plus abandoned temp files.
    /// Only files last written before <paramref name="olderThanUtc"/> are touched, so thumbnails generated
    /// after the ID snapshot was taken (e.g. by a concurrent scan) survive.
    /// </summary>
    /// <returns>The number of files deleted.</returns>
    Task<int> CleanOrphansAsync(IReadOnlySet<int> knownIds, DateTime olderThanUtc, CancellationToken ct = default);

    /// <summary>How much disk the thumbnails of one size use.</summary>
    Task<CacheUsage> GetUsageAsync(int size, CancellationToken ct = default);

    /// <summary>
    /// Deletes the least recently used thumbnails of one size until they fit in <paramref name="maxBytes"/>,
    /// going a little below so the next trim isn't due right away. 0 deletes them all.
    /// </summary>
    /// <returns>What was deleted.</returns>
    Task<CacheUsage> TrimAsync(int size, long maxBytes, CancellationToken ct = default);

    /// <summary>Deletes cache folders left by older thumbnail versions.</summary>
    Task DeleteStaleVersionsAsync(CancellationToken ct = default);
}
