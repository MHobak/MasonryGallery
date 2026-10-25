namespace MasonryGallery.Application.Ports;

using MasonryGallery.Domain;

public record MediaMetadata(int Width, int Height, long? DurationMs, DateTime? DateTaken, int? Rotation, bool NativePlayable);

public interface IMediaProbe
{
    ValueTask<Domain.Result<MediaMetadata>> ProbeAsync(string filePath, MediaKind kind, CancellationToken ct = default);
}
