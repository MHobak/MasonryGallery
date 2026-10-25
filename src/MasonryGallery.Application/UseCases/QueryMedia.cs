namespace MasonryGallery.Application.UseCases;

using MasonryGallery.Application.Ports;
using MasonryGallery.Domain;

public sealed class QueryMedia
{
    private readonly IMediaRepository _mediaRepository;

    public QueryMedia(IMediaRepository mediaRepository)
        => _mediaRepository = mediaRepository;

    public async Task<(IReadOnlyList<MediaItem> Items, bool HasMore)> ExecuteAsync(
        int limit = 200,
        DateTime? lastDateTaken = null,
        int? lastId = null,
        LibraryId? libraryId = null,
        MediaKind? kind = null,
        string? tag = null,
        string? search = null,
        CancellationToken ct = default)
    {
        var (items, hasMore) = await _mediaRepository.QueryPageAsync(
            limit, lastDateTaken, lastId, libraryId, kind, tag, search, ct);
        return (items.ToList(), hasMore);
    }
}
