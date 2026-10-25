namespace MasonryGallery.Application.Ports;

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

    /// <summary>Deletes cache folders left by older thumbnail versions.</summary>
    Task DeleteStaleVersionsAsync(CancellationToken ct = default);
}
