namespace MasonryGallery.Benchmarks;

using SkiaSharp;

/// <summary>
/// The synthetic library behind the budgets (design §12): many tiny but valid JPEGs for indexing and querying,
/// plus a few hundred camera-size photos for thumbnail throughput, where file size and pixels matter.
/// </summary>
public static class Dataset
{
    public const int DefaultFiles = 100_000;
    public const int Photos = 300;
    private const int FilesPerFolder = 500;

    public static string Stubs(string root) => Path.Combine(root, "stubs");
    public static string PhotosDir(string root) => Path.Combine(root, "photos");

    public static int Generate(string root, int files)
    {
        var stub = Jpeg(16, 12, SKColors.SlateBlue, 70);
        var start = DateTime.UtcNow.AddYears(-3);
        for (var i = 0; i < files; i++)
        {
            // Two levels of folders, like year/event, so the walk recurses as it would on a real library.
            var dir = Path.Combine(Stubs(root), $"{i / (FilesPerFolder * 20):D3}", $"event-{i / FilesPerFolder:D4}");
            if (i % FilesPerFolder == 0)
                Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"IMG_{i:D6}.jpg");
            File.WriteAllBytes(path, stub);
            File.SetLastWriteTimeUtc(path, start.AddMinutes(i));
        }

        // Decode cost depends on pixels and file size, not on files being different: render one photo (the noise
        // takes seconds) and write it under many names.
        Directory.CreateDirectory(PhotosDir(root));
        var photo = Jpeg(4000, 3000, SKColors.Peru, 90, noise: true);
        for (var i = 0; i < Photos; i++)
            File.WriteAllBytes(Path.Combine(PhotosDir(root), $"photo_{i:D4}.jpg"), photo);
        Console.WriteLine($"Generated {files:N0} stub files and {Photos} 12 MP photos under {root}");
        return 0;
    }

    // Noise keeps the JPEG realistically large (a flat colour would compress to almost nothing and decode instantly).
    private static byte[] Jpeg(int w, int h, SKColor color, int quality, bool noise = false)
    {
        using var bmp = new SKBitmap(w, h);
        bmp.Erase(color);
        if (noise)
        {
            using var canvas = new SKCanvas(bmp);
            using var paint = new SKPaint { Shader = SKShader.CreatePerlinNoiseFractalNoise(0.02f, 0.02f, 4, 1) };
            paint.BlendMode = SKBlendMode.Overlay;
            canvas.DrawRect(0, 0, w, h, paint);
        }
        return SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, quality).ToArray();
    }
}
