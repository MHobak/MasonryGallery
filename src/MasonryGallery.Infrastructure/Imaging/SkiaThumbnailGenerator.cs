namespace MasonryGallery.Infrastructure.Imaging;

using MasonryGallery.Application.Pipeline;
using MasonryGallery.Application.Ports;
using MasonryGallery.Domain;
using Microsoft.Extensions.Logging;
using SkiaSharp;

public sealed class SkiaThumbnailGenerator : IThumbnailGenerator
{
    // A decoded pixel is 4 bytes. The header of a tiny file can claim 100,000 x 100,000 pixels (40 GB); never
    // allocate for more than this. JPEGs decode scaled (1/8 of a 100 MP photo is 1.6 MP), so this only refuses
    // other formats that big.
    internal const long MaxDecodePixels = 100_000_000;

    // Decodes above this size (64 MB+) run one at a time, so a burst of huge PNGs on many workers can't
    // each take hundreds of megabytes at once (design §10: decode concurrency capped by memory).
    private const long LargeDecodePixels = 16_000_000;
    private static readonly SemaphoreSlim LargeDecodes = new(1, 1);

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
                return Result<ThumbnailImage>.Failure(
                    (long)codec.Info.Width * codec.Info.Height > MaxDecodePixels
                        ? $"Image is too large to preview ({codec.Info.Width} x {codec.Info.Height})"
                        : "Cannot decode image");

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
            var pixels = (long)size.Width * size.Height;
            if (pixels > MaxDecodePixels)
                continue;
            var large = pixels > LargeDecodePixels;
            if (large) LargeDecodes.Wait();
            try
            {
                var target = new SKImageInfo(size.Width, size.Height, SKImageInfo.PlatformColorType,
                    info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);
                var bitmap = new SKBitmap(target);
                // Allocation can still fail (memory pressure); a null buffer must never reach the decoder.
                if (bitmap.GetPixels() == IntPtr.Zero)
                {
                    bitmap.Dispose();
                    continue;
                }
                var result = codec.GetPixels(target, bitmap.GetPixels());
                // A truncated file still yields a usable picture.
                if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
                    return bitmap;
                bitmap.Dispose();
            }
            finally
            {
                if (large) LargeDecodes.Release();
            }
        }
        return null;
    }
}
