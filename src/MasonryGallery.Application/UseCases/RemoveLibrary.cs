namespace MasonryGallery.Application.UseCases;

using MasonryGallery.Application.Ports;
using MasonryGallery.Domain;

/// <summary>Forgets a library: its index rows, tags, favorites and thumbnails. Never touches the media files.</summary>
public sealed class RemoveLibrary
{
    private readonly ILibraryRepository _libraryRepository;
    private readonly IMediaRepository _mediaRepository;
    private readonly IThumbnailStore _thumbnailStore;
    private readonly IClock _clock;

    public RemoveLibrary(
        ILibraryRepository libraryRepository,
        IMediaRepository mediaRepository,
        IThumbnailStore thumbnailStore,
        IClock clock)
    {
        _libraryRepository = libraryRepository;
        _mediaRepository = mediaRepository;
        _thumbnailStore = thumbnailStore;
        _clock = clock;
    }

    public async Task<Result> ExecuteAsync(LibraryId libraryId, CancellationToken ct = default)
    {
        var library = await _libraryRepository.GetByIdAsync(libraryId, ct);
        if (!library.IsSuccess)
            return Result.Failure(library.Error!);
        // Deleting under a running scan would race its upserts back into existence.
        if (library.Value!.IsScanning)
            return Result.Failure("Wait for the scan to finish before removing this library.");

        var deleted = await _libraryRepository.DeleteAsync(libraryId, ct);
        if (!deleted.IsSuccess)
            return deleted;

        // The snapshot is taken after the delete, so every thumbnail of the removed library is an orphan.
        var snapshotTaken = _clock.UtcNow;
        await _thumbnailStore.CleanOrphansAsync(await _mediaRepository.GetAllIdsAsync(ct), snapshotTaken, ct);
        return Result.Success();
    }
}
