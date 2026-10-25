namespace MasonryGallery.Application.UseCases;

using MasonryGallery.Application.Ports;

public sealed class GetLayoutItems
{
    private readonly IMediaRepository _mediaRepository;

    public GetLayoutItems(IMediaRepository mediaRepository)
        => _mediaRepository = mediaRepository;

    /// <summary>Every matching item in display order; the layout runs over all of them, not over pages.</summary>
    public Task<IReadOnlyList<LibraryItem>> ExecuteAsync(MediaFilter? filter = null, CancellationToken ct = default)
        => _mediaRepository.GetLayoutItemsAsync(filter ?? new MediaFilter(), ct);
}
