namespace MasonryGallery.Application.Ports;

using MasonryGallery.Domain;

public record ThumbnailJob(int MediaId, string FilePath, MediaKind Kind, int ThumbVersion, long? DurationMs = null);

/// <param name="Webp">Encoded thumbnail.</param>
/// <param name="AverageRgb">Average colour as 0xRRGGBB, shown on the tile until the image loads.</param>
public record ThumbnailImage(byte[] Webp, int AverageRgb);

public interface IThumbnailGenerator
{
    ValueTask<Result<ThumbnailImage>> GenerateSmallAsync(ThumbnailJob job, CancellationToken ct = default);
    ValueTask<Result<ThumbnailImage>> GenerateMediumAsync(ThumbnailJob job, CancellationToken ct = default);
}
