namespace MasonryGallery.Infrastructure.Tests;

using FluentAssertions;
using MasonryGallery.Application.Ports;
using MasonryGallery.Domain;
using MasonryGallery.Infrastructure.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

public sealed class ImagingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mg-img-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>Left half red, right half blue, so any rotation is visible in the output.</summary>
    private static SKBitmap Halves(int w, int h)
    {
        var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Blue);
        canvas.DrawRect(0, 0, w / 2f, h, new SKPaint { Color = SKColors.Red });
        return bmp;
    }

    /// <summary>JPEG with an APP1 block: IFD0 {Orientation, ExifIFD pointer}, Exif IFD {DateTimeOriginal}.</summary>
    private static byte[] JpegWithExif(SKBitmap bmp, ushort orientation, string dateTimeOriginal)
    {
        using var image = SKImage.FromBitmap(bmp);
        var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();

        var tiff = new List<byte>();
        void U16(int v) { tiff.Add((byte)v); tiff.Add((byte)(v >> 8)); }
        void U32(int v) { U16(v & 0xFFFF); U16(v >>> 16); }
        tiff.AddRange("II"u8.ToArray()); U16(42); U32(8);           // little-endian header, IFD0 at 8
        U16(2);                                                      // IFD0: 2 entries, ends at 8+2+24+4 = 38
        U16(0x0112); U16(3); U32(1); U16(orientation); U16(0);       //   Orientation SHORT
        U16(0x8769); U16(4); U32(1); U32(38);                        //   Exif IFD pointer
        U32(0);
        U16(1);                                                      // Exif IFD at 38: 1 entry, ends at 56
        U16(0x9003); U16(2); U32(20); U32(56);                       //   DateTimeOriginal ASCII[20] at 56
        U32(0);
        tiff.AddRange(System.Text.Encoding.ASCII.GetBytes(dateTimeOriginal + "\0"));

        byte[] payload = [.. "Exif\0\0"u8.ToArray(), .. tiff];
        var length = payload.Length + 2;
        return [.. jpeg[..2], 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload, .. jpeg[2..]];
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task Probe_reads_exif_date_and_reports_upright_size()
    {
        using var bmp = Halves(400, 200);
        var path = Write("rotated.jpg", JpegWithExif(bmp, orientation: 6, "2019:07:04 15:30:00"));

        var r = await new SkiaProbe(NullLogger<SkiaProbe>.Instance).ProbeAsync(path, MediaKind.Image);

        r.Error.Should().BeNull();
        (r.Value!.Width, r.Value.Height).Should().Be((200, 400), "orientation 6 stores the photo a quarter turn off");
        r.Value.DateTaken.Should().Be(new DateTime(2019, 7, 4, 15, 30, 0, DateTimeKind.Local).ToUniversalTime());
    }

    [Fact]
    public void Exif_reader_ignores_files_without_exif()
    {
        using var bmp = Halves(40, 20);
        using var image = SKImage.FromBitmap(bmp);
        ExifReader.ReadDateTaken(image.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray()).Should().BeNull();
        ExifReader.ReadDateTaken("not a jpeg"u8).Should().BeNull();
    }

    [Theory]
    // Orientation 6 = rotate 90° clockwise to view: the stored left (red) half ends up on top.
    [InlineData(6, 160, 320, "red", "blue")]
    // Orientation 8 = rotate 90° counter-clockwise: the stored left half ends up at the bottom.
    [InlineData(8, 160, 320, "blue", "red")]
    // Orientation 3 = 180°: no axis swap, red moves to the right.
    [InlineData(3, 320, 160, "blue", "blue")]
    [InlineData(1, 320, 160, "red", "red")]
    public async Task Thumbnail_applies_exif_orientation(ushort orientation, int width, int height, string topLeft, string bottomLeft)
    {
        using var bmp = Halves(1600, 800); // large enough to take the scaled-decode path
        var path = Write($"o{orientation}.jpg", JpegWithExif(bmp, orientation, "2019:07:04 15:30:00"));

        var r = await new SkiaThumbnailGenerator(NullLogger<SkiaThumbnailGenerator>.Instance)
            .GenerateSmallAsync(new ThumbnailJob(1, path, MediaKind.Image, 2));

        r.Error.Should().BeNull();
        using var thumb = SKBitmap.Decode(r.Value!.Webp);
        (thumb.Width, thumb.Height).Should().Be((width, height));
        Name(thumb.GetPixel(5, 5)).Should().Be(topLeft);
        Name(thumb.GetPixel(5, height - 6)).Should().Be(bottomLeft);
    }

    private static string Name(SKColor c) => c.Red > c.Blue ? "red" : "blue";

    [Fact]
    public async Task Large_jpeg_is_decoded_scaled_to_exact_thumbnail_size()
    {
        using var bmp = Halves(3000, 2000);
        using var image = SKImage.FromBitmap(bmp);
        var path = Write("big.jpg", image.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray());

        var r = await new SkiaThumbnailGenerator(NullLogger<SkiaThumbnailGenerator>.Instance)
            .GenerateSmallAsync(new ThumbnailJob(1, path, MediaKind.Image, 2));

        using var thumb = SKBitmap.Decode(r.Value!.Webp);
        (thumb.Width, thumb.Height).Should().Be((320, 213));
    }
}
