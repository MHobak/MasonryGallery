namespace MasonryGallery.Application.Ports;

using System.Collections.ObjectModel;
using MasonryGallery.Domain;

public interface IMediaRepository
{
    Task<Result<int>> UpsertAsync(MediaItem item, CancellationToken ct = default);
    Task<int> UpsertBatchAsync(IEnumerable<MediaItem> items, CancellationToken ct = default);
    Task<(ReadOnlyCollection<MediaItem> Items, bool HasMore)> QueryPageAsync(
        int limit,
        DateTime? lastDateTaken,
        int? lastId,
        LibraryId? libraryId,
        MediaKind? kind,
        string? tag,
        string? search,
        CancellationToken ct = default);
    Task<MediaItem?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<ReadOnlyCollection<MediaItem>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken ct = default);
    /// <summary>Layout records for every item matching <paramref name="filter"/>, in display order. Disabled libraries are hidden.</summary>
    Task<IReadOnlyList<LibraryItem>> GetLayoutItemsAsync(MediaFilter filter, CancellationToken ct = default);
    /// <summary>File names for the given IDs (tile labels); IDs not found are absent.</summary>
    Task<IReadOnlyDictionary<int, string>> GetFileNamesAsync(IReadOnlyList<int> ids, CancellationToken ct = default);
    Task SetFavoriteAsync(int mediaId, bool favorite, CancellationToken ct = default);
    /// <summary>Tags in use on live media, with how many items carry each, by name.</summary>
    Task<IReadOnlyList<TagCount>> ListTagsAsync(CancellationToken ct = default);
    /// <summary>
    /// Tags whose name starts with <paramref name="prefix"/> (any case) or contains it after a word break ("_hair"
    /// style names), most used first. For autocomplete; counts include every library.
    /// </summary>
    Task<IReadOnlyList<TagCount>> SearchTagsAsync(string prefix, int limit, CancellationToken ct = default);
    /// <summary>
    /// Replaces the sidecar-derived tags and details of the given paths; null data clears them (sidecar gone or
    /// unreadable). Tags the user added in the app are kept.
    /// </summary>
    Task SaveSidecarsAsync(int libraryId, IReadOnlyList<(string RelPath, SidecarData? Data)> sidecars, CancellationToken ct = default);
    /// <summary>Creator, source and the other sidecar details of one item (its tags come from <see cref="GetTagsForMediaAsync"/>).</summary>
    Task<SidecarData?> GetSidecarAsync(int mediaId, CancellationToken ct = default);
    /// <summary>Live items whose probe or thumbnail failed, newest first.</summary>
    Task<IReadOnlyList<FailedItem>> GetFailedAsync(int limit, CancellationToken ct = default);
    Task<int> CountFailedAsync(CancellationToken ct = default);
    Task MarkScanCompleteAsync(int libraryId, int scanId, CancellationToken ct = default);
    /// <returns>The number of rows permanently deleted (past retention).</returns>
    Task<int> SoftDeleteOldScansAsync(int libraryId, int scanId, int retentionDays, CancellationToken ct = default);
    Task<int> CountLiveAsync(int libraryId, CancellationToken ct = default);
    /// <summary>Soft-deletes each path, and everything under it when it was a folder.</summary>
    Task<int> SoftDeletePathsAsync(int libraryId, IReadOnlyCollection<string> relPaths, CancellationToken ct = default);
    /// <summary>Soft-deletes rows under <paramref name="relDirectory"/> that the scan <paramref name="scanId"/> did not see.</summary>
    Task<int> SoftDeleteUnseenUnderAsync(int libraryId, string relDirectory, int scanId, CancellationToken ct = default);
    /// <summary>Every media ID in the DB, soft-deleted included (their thumbnails are kept until hard delete).</summary>
    Task<IReadOnlySet<int>> GetAllIdsAsync(CancellationToken ct = default);
    /// <summary>Stored change-detection state for the given paths, including soft-deleted rows. Missing paths are new.</summary>
    Task<IReadOnlyDictionary<string, ScanState>> GetScanStatesAsync(int libraryId, IReadOnlyList<string> relPaths, CancellationToken ct = default);
    Task<int> CountPendingThumbnailsAsync(int libraryId, CancellationToken ct = default);
    /// <summary>Re-queues thumbnails made by an older generator version (including failed ones).</summary>
    Task<int> ResetOutdatedThumbnailsAsync(int libraryId, int currentVersion, CancellationToken ct = default);
    /// <summary>
    /// Next pending thumbnails, newest first, strictly after the keyset cursor (null = from the start).
    /// The cursor keeps each call walking forward through the index instead of rescanning finished rows.
    /// </summary>
    Task<ReadOnlyCollection<MediaItem>> GetPendingThumbnailsAsync(
        int libraryId, int count, (DateTime DateTaken, int Id)? after, CancellationToken ct = default);
    /// <summary>The given IDs that belong to the library and still need a thumbnail.</summary>
    Task<ReadOnlyCollection<MediaItem>> GetPendingThumbnailsByIdAsync(
        int libraryId, IReadOnlyList<int> ids, CancellationToken ct = default);
    /// <param name="averageRgb">Placeholder colour (0xRRGGBB); null leaves the stored one.</param>
    Task UpdateThumbAsync(int mediaId, ThumbState state, int thumbVersion, int? averageRgb = null, CancellationToken ct = default);
    Task<ReadOnlyCollection<Tag>> GetTagsForMediaAsync(int mediaId, CancellationToken ct = default);
    Task SetTagsAsync(int mediaId, IEnumerable<string> tagNames, CancellationToken ct = default);
    Task UpsertTagAsync(string name, CancellationToken ct = default);
    Task<int> GetTagIdAsync(string name, CancellationToken ct = default);
    Task<ReadOnlyCollection<(int MediaId, string TagName)>> SearchByTagAsync(string tagName, CancellationToken ct = default);
    Task<bool> PathExistsAsync(LibraryId libraryId, string relPath, CancellationToken ct = default);
    Task<ReadOnlyCollection<MediaItem>> GetChangedSinceAsync(int libraryId, DateTime since, CancellationToken ct = default);
}

/// <param name="SidecarModifiedUtc">When the sidecar that was last read was written; null when there was none.</param>
public record ScanState(long SizeBytes, DateTime ModifiedUtc, ProbeState ProbeState, DateTime? SidecarModifiedUtc = null);

public record LibraryItem(int Id, int Width, int Height, MediaKind Kind, bool ThumbReady, byte[]? Placeholder, bool Favorite = false);

public enum MediaSort
{
    NewestFirst,
    OldestFirst,
    RecentlyAdded,
    Name,
    /// <summary>Shuffled by <see cref="MediaFilter.Seed"/>: the same seed keeps the same order across refreshes.</summary>
    Random
}

/// <summary>What the gallery shows. Null members don't filter; dates are inclusive bounds on DateTaken (UTC).</summary>
/// <param name="Tags">Items must carry every one of these tags.</param>
/// <param name="ExcludedTags">Items must carry none of these tags.</param>
/// <param name="LibraryIds">Items must come from one of these libraries; null or empty means all.</param>
public sealed record MediaFilter(
    IReadOnlyList<LibraryId>? LibraryIds = null,
    MediaKind? Kind = null,
    string? Search = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? ExcludedTags = null,
    DateTime? From = null,
    DateTime? To = null,
    bool FavoritesOnly = false,
    MediaSort Sort = MediaSort.NewestFirst,
    int Seed = 0);

public record TagCount(string Name, int Count, string? Category = null);

/// <param name="Stage">"probe" (unreadable file) or "thumbnail".</param>
public record FailedItem(int Id, string FileName, string RelPath, string LibraryName, string Stage);
