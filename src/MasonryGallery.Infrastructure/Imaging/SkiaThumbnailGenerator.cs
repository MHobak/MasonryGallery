namespace MasonryGallery.Infrastructure.Imaging;

using MasonryGallery.Application.Pipeline;
using MasonryGallery.Application.Ports;
using MasonryGallery.Domain;
using Microsoft.Extensions.Logging;
using SkiaSharp;

public sealed class SkiaThumbnailGenerator : IThumbnailGenerator
{
    private readonly ILogger<SkiaThumbnailGenerator> _logger;

    public SkiaThumbnailGenerator(ILogger<SkiaThumbnailGenerator> logger)
        => _logger = logger;

    public ValueTask<Result<ThumbnailImage>> GenerateSmallAsync(ThumbnailJob job, CancellationToken ct = default)
        => ValueTask.FromResult(Generate(job, ThumbnailFormat.Small));

    public ValueTask<Result<ThumbnailImage>> GenerateMediumAsync(ThumbnailJob job, CancellationToken ct = default)
        => ValueTask.FromResult(Generate(job, ThumbnailFormat.Medium));

    private Result<ThumbnailImage> Generate(ThumbnailJob job, int longEdge)
    {
        if (job.Kind != MediaKind.Image)
            return Result<ThumbnailImage>.Failure($"Thumbnail not implemented for {job.Kind}");

        try
        {
            using var codec = SKCodec.Create(job.FilePath);
            if (codec == null)
                return Result<ThumbnailImage>.Failure("Cannot open image");
            if (codec.Info.Width == 0 || codec.Info.Height == 0)
                return Result<ThumbnailImage>.Failure("Invalid image dimensions");

            using var decoded = DecodeScaled(codec, longEdge);
            if (decoded == null)
                return Result<ThumbnailImage>.Failure("Cannot decode image");

            return ThumbnailEncoder.Encode(decoded, codec.EncodedOrigin, longEdge, job.Quality);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Thumbnail generation failed for media {MediaId}", job.MediaId);
            return Result<ThumbnailImage>.Failure(ex.Message);
        }
    }

    // JPEG decodes natively at k/8 scale (DCT scaling), several times faster than decoding in full and resizing.
    // Pick the smallest k/8 that still covers the target; codecs that can't scale fall back to full size.
    private static SKBitmap? DecodeScaled(SKCodec codec, int longEdge)
    {
        var info = codec.Info;
        var full = Math.Max(info.Width, info.Height);
        var sizes = new List<SKSizeI>(2);
        if (full > longEdge)
        {
            var eighths = Math.Clamp((int)Math.Ceiling(8.0 * longEdge / full), 1, 8);
            var scaled = codec.GetScaledDimensions(eighths / 8f);
            // Rounding may land a pixel or two short; that much upscaling is invisible.
            if (Math.Max(scaled.Width, scaled.Height) >= longEdge * 0.98)
                sizes.Add(scaled);
        }
        sizes.Add(info.Size);

        foreach (var size in sizes.Distinct())
        {
            var target = new SKImageInfo(size.Width, size.Height, SKImageInfo.PlatformColorType,
                info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);
            var bitmap = new SKBitmap(target);
            var result = codec.GetPixels(target, bitmap.GetPixels());
            // A truncated file still yields a usable picture.
            if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
                return bitmap;
            bitmap.Dispose();
        }
        return null;
    }
}
