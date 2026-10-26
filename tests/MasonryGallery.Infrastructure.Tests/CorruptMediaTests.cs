namespace MasonryGallery.Infrastructure.Tests;

using System.Buffers.Binary;
using System.Diagnostics;
using FluentAssertions;
using MasonryGallery.Application.Pipeline;
using MasonryGallery.Application.Ports;
using MasonryGallery.Application.UseCases;
using MasonryGallery.Domain;
using MasonryGallery.Infrastructure.FileSystem;
using MasonryGallery.Infrastructure.Imaging;
using MasonryGallery.Infrastructure.Persistence;
using MasonryGallery.Infrastructure.Thumbnails;
using MasonryGallery.Infrastructure.Video;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

/// <summary>
/// Hardening (design §6 of the roadmap): broken, truncated and hostile files must end up marked as failed,
/// never crash a scan, stall it, or exhaust memory, and must not stop the good files next to them.
/// </summary>
public sealed class CorruptMediaTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mg-corrupt-").FullName;
    private string Lib => Path.Combine(_dir, "lib");

    public CorruptMediaTests() => Directory.CreateDirectory(Lib);

    public void Dispose() => Directory.Delete(_dir, true);

    private void Write(string name, byte[] bytes)
    {
        var path = Path.Combine(Lib, name);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
    }

    private static byte[] Jpeg(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        bmp.Erase(SKColors.SeaGreen);
        return SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
    }

    private static byte[] Png(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        bmp.Erase(SKColors.Goldenrod);
        return SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    // A valid PNG header claiming a 100,000 x 100,000 RGB image (40 GB decoded) in a few dozen bytes.
    private static byte[] PngBomb()
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, 100_000);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), 100_000);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // truecolour
        var png = new List<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        png.AddRange(Chunk("IHDR", ihdr));
        png.AddRange(Chunk("IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01])); // an empty zlib stream
        png.AddRange(Chunk("IEND", []));
        return [.. png];

        static byte[] Chunk(string type, byte[] data)
        {
            var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            var chunk = new byte[12 + data.Length];
            BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
            typed.CopyTo(chunk, 4);
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32(typed));
            return chunk;
        }

        static uint Crc32(byte[] bytes)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in bytes)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return ~crc;
        }
    }

    [Fact]
    public async Task Broken_files_are_marked_failed_and_the_scan_carries_on()
    {
        var random = new Random(7);
        var noise = new byte[64 * 1024];
        random.NextBytes(noise);
        var jpeg = Jpeg(1200, 800);
        var badExif = jpeg.Take(2).Concat(new byte[] { 0xFF, 0xE1, 0x00, 0x40 }).Concat(noise.Take(62)).Concat(jpeg.Skip(2)).ToArray();

        Write("good.jpg", jpeg);
        Write("good.png", Png(300, 200));
        Write("empty.jpg", []);
        Write("noise.jpg", noise);
        Write("noise.png", noise);
        Write("truncated.jpg", jpeg[..(jpeg.Length / 3)]);
        Write("truncated.png", Png(300, 200)[..200]);
        Write("bad-exif.jpg", badExif);
        Write("bomb.png", PngBomb());
        Write("text.mp4", System.Text.Encoding.UTF8.GetBytes("not a video at all"));
        Write("noise.webm", noise);

        var factory = new SqliteConnectionFactory(Path.Combine(_dir, "t.db"), NullLogger<SqliteConnectionFactory>.Instance);
        await SchemaMigrator.MigrateAsync(factory, NullLogger.Instance);
        var libs = new SqliteLibraryRepository(factory, NullLogger<SqliteLibraryRepository>.Instance);
        var media = new SqliteMediaRepository(factory, NullLogger<SqliteMediaRepository>.Instance);
        var ffmpeg = FfmpegLocator.Locate() ?? "ffmpeg";
        var ffprobe = FfmpegLocator.LocateProbe() ?? "ffprobe";
        var scan = new Scan(libs, media, new FileSystemEnumerator(NullLogger<FileSystemEnumerator>.Instance),
            new CompositeProbe(new SkiaProbe(NullLogger<SkiaProbe>.Instance), new FfprobeProbe(ffprobe, NullLogger<FfprobeProbe>.Instance)),
            new CompositeThumbnailGenerator(new SkiaThumbnailGenerator(NullLogger<SkiaThumbnailGenerator>.Instance),
                new FfmpegThumbnailGenerator(ffmpeg, NullLogger<FfmpegThumbnailGenerator>.Instance)),
            new ShardedThumbnailStore(Path.Combine(_dir, "thumbs"), "v2", NullLogger<ShardedThumbnailStore>.Instance),
            new SystemClock(), new PipelineProgressReporter(), NullLogger<Scan>.Instance);
        var library = (await new AddLibrary(libs).ExecuteAsync("Lib", Lib)).Value!;
        var memoryBefore = Process.GetCurrentProcess().WorkingSet64;

        var result = await scan.ExecuteAsync(library.Id).WaitAsync(TimeSpan.FromSeconds(60));

        result.Error.Should().BeNull();
        var (items, _) = await media.QueryPageAsync(100, null, null, null, null, null, null);
        var byName = items.ToDictionary(i => i.FileName);
        byName.Should().HaveCount(11, "broken files are listed (as problems), not dropped");

        byName["good.jpg"].ThumbState.Should().Be(ThumbState.Done);
        byName["good.png"].ThumbState.Should().Be(ThumbState.Done);
        foreach (var name in new[] { "empty.jpg", "noise.jpg", "noise.png", "text.mp4", "noise.webm" })
            byName[name].ProbeState.Should().Be(ProbeState.Failed, $"{name} can't be read");
        byName["bomb.png"].ThumbState.Should().Be(ThumbState.Failed, "decoding it would take 40 GB");
        // Truncated files may still give a partial picture; what matters is that they settled.
        foreach (var name in new[] { "truncated.jpg", "truncated.png", "bad-exif.jpg" })
            byName[name].ThumbState.Should().NotBe(ThumbState.Pending, $"{name} must not stay queued forever");

        (await media.CountFailedAsync()).Should().BeGreaterThanOrEqualTo(6);
        (Process.GetCurrentProcess().WorkingSet64 - memoryBefore).Should().BeLessThan(1L << 30, "nothing near 40 GB was allocated");
    }

    [Fact]
    public async Task Oversized_images_are_refused_with_a_clear_reason()
    {
        Write("bomb.png", PngBomb());

        var result = await new SkiaThumbnailGenerator(NullLogger<SkiaThumbnailGenerator>.Instance)
            .GenerateSmallAsync(new ThumbnailJob(1, Path.Combine(Lib, "bomb.png"), MediaKind.Image, 2));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("too large").And.Contain("100000 x 100000");
    }
}
