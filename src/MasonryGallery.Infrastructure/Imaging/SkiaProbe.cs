namespace MasonryGallery.Infrastructure.Imaging;

using MasonryGallery.Application.Ports;
using MasonryGallery.Domain;
using Microsoft.Extensions.Logging;
using SkiaSharp;

public sealed class SkiaProbe : IMediaProbe
{
    private readonly ILogger<SkiaProbe> _logger;

    public SkiaProbe(ILogger<SkiaProbe> logger)
        => _logger = logger;

    public ValueTask<Result<MediaMetadata>> ProbeAsync(string filePath, MediaKind kind, CancellationToken ct = default)
        => ValueTask.FromResult(Probe(filePath, kind));

    private Result<MediaMetadata> Probe(string filePath, MediaKind kind)
    {
        if (kind != MediaKind.Image)
            return Result<MediaMetadata>.Failure("SkiaProbe only handles images");

        try
        {
            // Reads the header only: dimensions and the EXIF orientation Skia parses itself.
            using var codec = SKCodec.Create(filePath);
            if (codec == null)
                return Result<MediaMetadata>.Failure("Cannot create codec for image");

            var width = codec.Info.Width;
            var height = codec.Info.Height;
            var origin = codec.EncodedOrigin;
            // Store the upright size: the layout needs the aspect ratio the thumbnail will have.
            if (ImageOrientation.SwapsAxes(origin))
                (width, height) = (height, width);

            var dateTaken = codec.EncodedFormat == SKEncodedImageFormat.Jpeg
                ? ExifReader.ReadDateTaken(filePath)
                : null;

            return new MediaMetadata(width, height, null, dateTaken, (int)origin, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe image {Path}", filePath);
            return Result<MediaMetadata>.Failure(ex.Message);
        }
    }
}
