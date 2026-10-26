namespace MasonryGallery.Infrastructure.Imaging;

using MasonryGallery.Application.Ports;
using SkiaSharp;

/// <summary>Resize, orient and WebP-encode a decoded frame; shared by the image and video generators.</summary>
internal static class ThumbnailEncoder
{
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    public static ThumbnailImage Encode(SKBitmap source, SKEncodedOrigin origin, int longEdge, int quality)
    {
        var scale = Math.Min(1.0, (double)longEdge / Math.Max(source.Width, source.Height));
        using var resized = scale < 1.0
            ? source.Resize(new SKImageInfo(
                Math.Max(1, (int)Math.Round(source.Width * scale)),
                Math.Max(1, (int)Math.Round(source.Height * scale)),
                source.ColorType, source.AlphaType), Sampling)
            : null;
        var sized = resized ?? source;

        // Rotate after resizing: the transform then touches 320 px, not the original.
        using var oriented = ImageOrientation.Apply(sized, origin);
        var final = oriented ?? sized;

        using var image = SKImage.FromBitmap(final);
        using var data = image.Encode(SKEncodedImageFormat.Webp, quality);
        return new ThumbnailImage(data.ToArray(), AverageRgb(final));
    }

    private static int AverageRgb(SKBitmap bitmap)
    {
        // A mipmapped downscale to a single pixel averages the whole image.
        using var pixel = bitmap.Resize(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul), Sampling);
        var c = pixel.GetPixel(0, 0);
        return (c.Red << 16) | (c.Green << 8) | c.Blue;
    }
}

/// <summary>EXIF orientation (1-8, as <see cref="SKEncodedOrigin"/>) to upright pixels.</summary>
internal static class ImageOrientation
{
    /// <summary>Orientations 5-8 store the image rotated a quarter turn, so width and height trade places.</summary>
    public static bool SwapsAxes(SKEncodedOrigin origin) => (int)origin >= 5;

    /// <returns>A new upright bitmap, or null when <paramref name="origin"/> is already upright.</returns>
    public static SKBitmap? Apply(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft || (int)origin is < 1 or > 8)
            return null;

        float w = source.Width, h = source.Height;
        // Maps stored pixel (x, y) to its upright position: x' = sx*x + kx*y + tx, y' = ky*x + sy*y + ty.
        var m = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),     // 2: mirror
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), // 3: 180°
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),   // 4: flip
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),       // 5: transpose
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),     // 6: 90° clockwise
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1), // 7: transverse
            _ => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),                            // 8: 90° counter-clockwise
        };

        var upright = SwapsAxes(origin)
            ? new SKBitmap(source.Height, source.Width, source.ColorType, source.AlphaType)
            : new SKBitmap(source.Info);
        using var canvas = new SKCanvas(upright);
        canvas.SetMatrix(m);
        canvas.DrawBitmap(source, 0, 0);
        return upright;
    }
}
