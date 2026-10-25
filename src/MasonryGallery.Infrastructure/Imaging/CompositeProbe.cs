namespace MasonryGallery.Infrastructure.Imaging;

using MasonryGallery.Application.Ports;
using MasonryGallery.Domain;

public sealed class CompositeProbe(IMediaProbe images, IMediaProbe videos) : IMediaProbe
{
    public ValueTask<Result<MediaMetadata>> ProbeAsync(string filePath, MediaKind kind, CancellationToken ct = default)
        => (kind == MediaKind.Image ? images : videos).ProbeAsync(filePath, kind, ct);
}
