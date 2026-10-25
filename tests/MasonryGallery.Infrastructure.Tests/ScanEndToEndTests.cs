namespace MasonryGallery.Infrastructure.Tests;

using FluentAssertions;
using Xunit;
using MasonryGallery.Application.Pipeline;
using MasonryGallery.Application.Ports;
using MasonryGallery.Application.UseCases;
using MasonryGallery.Domain;
using MasonryGallery.Infrastructure.FileSystem;
using MasonryGallery.Infrastructure.Imaging;
using MasonryGallery.Infrastructure.Persistence;
using MasonryGallery.Infrastructure.Thumbnails;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

public sealed class ScanEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mg-" + Guid.NewGuid().ToString("N"));

    public ScanEndToEndTests() => Directory.CreateDirectory(Path.Combine(_dir, "photos", "sub"));

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("MG_KEEP") is null) try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private void WritePng(string rel, int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        bmp.Erase(SKColors.CornflowerBlue);
        using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(_dir, "photos", rel), data.ToArray());
    }

    [Fact]
    public async Task Probe_reads_png_dimensions()
    {
        WritePng("p.png", 400, 200);
        var r = await new SkiaProbe(NullLogger<SkiaProbe>.Instance)
            .ProbeAsync(Path.Combine(_dir, "photos", "p.png"), MediaKind.Image);
        r.Error.Should().BeNull();
        (r.Value!.Width, r.Value.Height).Should().Be((400, 200));
    }

    [Fact]
    public async Task Thumbnail_is_generated_for_png()
    {
        WritePng("p.png", 400, 200);
        var r = await new SkiaThumbnailGenerator(NullLogger<SkiaThumbnailGenerator>.Instance)
            .GenerateSmallAsync(new ThumbnailJob(1, Path.Combine(_dir, "photos", "p.png"), MediaKind.Image, 1));
        r.Error.Should().BeNull();
        r.Value!.Webp.Should().NotBeEmpty();
        // Average of a solid CornflowerBlue (100, 149, 237) image, within WebP's lossy drift.
        var rgb = r.Value.AverageRgb;
        new[] { rgb >> 16, (rgb >> 8) & 0xFF, rgb & 0xFF }.Should().Equal([100, 149, 237],
            (actual, expected) => Math.Abs(actual - expected) <= 6);
    }

    [Fact]
    public async Task Scan_indexes_probes_and_thumbnails_and_survives_rescan()
    {
        WritePng("a.png", 400, 200);
        WritePng("sub/b.png", 100, 300);
        File.WriteAllText(Path.Combine(_dir, "photos", "notes.txt"), "ignored");

        var factory = new SqliteConnectionFactory(Path.Combine(_dir, "t.db"), NullLogger<SqliteConnectionFactory>.Instance);
        await SchemaMigrator.MigrateAsync(factory, NullLogger.Instance);

        var libs = new SqliteLibraryRepository(factory, NullLogger<SqliteLibraryRepository>.Instance);
        var media = new SqliteMediaRepository(factory, NullLogger<SqliteMediaRepository>.Instance);
        var thumbs = new ShardedThumbnailStore(Path.Combine(_dir, "thumbs"), "v1", NullLogger<ShardedThumbnailStore>.Instance);
        var scan = new Scan(libs, media,
            new FileSystemEnumerator(NullLogger<FileSystemEnumerator>.Instance),
            new SkiaProbe(NullLogger<SkiaProbe>.Instance),
            new SkiaThumbnailGenerator(NullLogger<SkiaThumbnailGenerator>.Instance),
            thumbs, new SystemClock(), new PipelineProgressReporter(), NullLogger<Scan>.Instance);

        var added = await new AddLibrary(libs).ExecuteAsync("Photos", Path.Combine(_dir, "photos"));
        added.IsSuccess.Should().BeTrue();
        (await new AddLibrary(libs).ExecuteAsync("Dup", Path.Combine(_dir, "photos"))).IsSuccess.Should().BeFalse();

        for (var pass = 0; pass < 2; pass++)
        {
            (await scan.ExecuteAsync(added.Value!.Id)).Error.Should().BeNull();

            var items = await new GetLayoutItems(media).ExecuteAsync();
            items.Should().HaveCount(2);
            items.Should().OnlyContain(i => i.ThumbReady);
            items.Select(i => (i.Width, i.Height)).Should().BeEquivalentTo([(400, 200), (100, 300)]);
            (await thumbs.GetAsync(items[0].Id, 320)).Should().NotBeNull();
        }

        var filtered = await new GetLayoutItems(media).ExecuteAsync(new MediaFilter(Search: "a.p"));
        filtered.Should().HaveCount(1);

        File.Delete(Path.Combine(_dir, "photos", "a.png"));
        await scan.ExecuteAsync(added.Value!.Id);
        (await new GetLayoutItems(media).ExecuteAsync()).Should().HaveCount(1);
    }

    private static readonly DateTime Taken = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    /// <summary>Counts probe calls and reports a fixed capture date, standing in for EXIF.</summary>
    private sealed class CountingProbe : IMediaProbe
    {
        private readonly SkiaProbe _inner = new(NullLogger<SkiaProbe>.Instance);
        public int Calls;

        public async ValueTask<Result<MediaMetadata>> ProbeAsync(string filePath, MediaKind kind, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            var r = await _inner.ProbeAsync(filePath, kind, ct);
            return r.IsSuccess ? r.Value! with { DateTaken = Taken } : r;
        }
    }

    private sealed record Rig(Scan Scan, SqliteMediaRepository Media, SqliteLibraryRepository Libs, LibraryId Id, CountingProbe Probe)
    {
        public async Task<int> CountAsync() => (await new GetLayoutItems(Media).ExecuteAsync()).Count;
        public async Task<LibraryStatus> StatusAsync() => (await Libs.GetByIdAsync(Id)).Value!.Status;
    }

    private async Task<Rig> SetupAsync()
    {
        WritePng("a.png", 400, 200);
        WritePng("sub/b.png", 100, 300);
        var factory = new SqliteConnectionFactory(Path.Combine(_dir, "t.db"), NullLogger<SqliteConnectionFactory>.Instance);
        await SchemaMigrator.MigrateAsync(factory, NullLogger.Instance);
        var libs = new SqliteLibraryRepository(factory, NullLogger<SqliteLibraryRepository>.Instance);
        var media = new SqliteMediaRepository(factory, NullLogger<SqliteMediaRepository>.Instance);
        var probe = new CountingProbe();
        var scan = new Scan(libs, media,
            new FileSystemEnumerator(NullLogger<FileSystemEnumerator>.Instance), probe,
            new SkiaThumbnailGenerator(NullLogger<SkiaThumbnailGenerator>.Instance),
            new ShardedThumbnailStore(Path.Combine(_dir, "thumbs"), "v1", NullLogger<ShardedThumbnailStore>.Instance),
            new SystemClock(), new PipelineProgressReporter(), NullLogger<Scan>.Instance);
        var id = (await new AddLibrary(libs).ExecuteAsync("Photos", Path.Combine(_dir, "photos"))).Value!.Id;
        (await scan.ExecuteAsync(id)).Error.Should().BeNull();
        return new Rig(scan, media, libs, id, probe);
    }

    [Fact]
    public async Task Unplugged_library_keeps_its_items_and_comes_back_online()
    {
        var rig = await SetupAsync();
        var photos = Path.Combine(_dir, "photos");

        Directory.Move(photos, photos + "-unplugged");
        (await rig.Scan.ExecuteAsync(rig.Id)).IsSuccess.Should().BeFalse();
        (await rig.CountAsync()).Should().Be(2);
        (await rig.StatusAsync()).Should().Be(LibraryStatus.Offline);

        Directory.Move(photos + "-unplugged", photos);
        (await rig.Scan.ExecuteAsync(rig.Id)).Error.Should().BeNull();
        (await rig.CountAsync()).Should().Be(2);
        (await rig.StatusAsync()).Should().Be(LibraryStatus.Online);
    }

    [Fact]
    public async Task Empty_mount_point_does_not_wipe_the_library()
    {
        var rig = await SetupAsync();
        var photos = Path.Combine(_dir, "photos");
        Directory.Move(photos, photos + "-nas");
        Directory.CreateDirectory(photos);

        (await rig.Scan.ExecuteAsync(rig.Id)).IsSuccess.Should().BeFalse();
        (await rig.CountAsync()).Should().Be(2);
        (await rig.StatusAsync()).Should().Be(LibraryStatus.Offline);
    }

    [Fact]
    public async Task File_that_returns_after_deletion_is_revived()
    {
        var rig = await SetupAsync();
        var a = Path.Combine(_dir, "photos", "a.png");
        File.Move(a, Path.Combine(_dir, "a.png"));
        await rig.Scan.ExecuteAsync(rig.Id);
        (await rig.CountAsync()).Should().Be(1);

        File.Move(Path.Combine(_dir, "a.png"), a);
        await rig.Scan.ExecuteAsync(rig.Id);
        (await rig.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Rescan_skips_probe_for_unchanged_files_and_keeps_probed_date()
    {
        var rig = await SetupAsync();
        rig.Probe.Calls.Should().Be(2);

        await rig.Scan.ExecuteAsync(rig.Id);
        rig.Probe.Calls.Should().Be(2);
        var (items, _) = await rig.Media.QueryPageAsync(10, null, null, null, null, null, null);
        items.Should().OnlyContain(i => i.DateTaken == Taken);

        WritePng("a.png", 500, 250);
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "photos", "a.png"), DateTime.UtcNow.AddMinutes(1));
        await rig.Scan.ExecuteAsync(rig.Id);
        rig.Probe.Calls.Should().Be(3);
        (await new GetLayoutItems(rig.Media).ExecuteAsync())
            .Should().Contain(i => i.Width == 500 && i.Height == 250);
    }

    [Theory]
    [InlineData("png", 2)]
    [InlineData("su", 1)]          // prefix match on the RelPath folder
    [InlineData("a", 1)]
    [InlineData("b\" OR \"", 0)]   // FTS syntax in user text is treated as literal
    public async Task Search_uses_full_text_index(string search, int expected)
    {
        var rig = await SetupAsync();
        var (items, _) = await rig.Media.QueryPageAsync(10, null, null, null, null, null, search);
        items.Should().HaveCount(expected);
    }

    [Fact]
    public async Task Video_thumbnail_is_webp_with_long_edge_320()
    {
        var ffmpeg = MasonryGallery.Infrastructure.Video.FfmpegLocator.Locate();
        if (ffmpeg is null) return; // no ffmpeg on this machine

        var clip = Path.Combine(_dir, "clip.webm");
        using (var make = System.Diagnostics.Process.Start(ffmpeg,
                   ["-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=360x640:duration=2", "-c:v", "libvpx-vp9", clip]))
            await make.WaitForExitAsync();

        var r = await new MasonryGallery.Infrastructure.Video.FfmpegThumbnailGenerator(ffmpeg,
                NullLogger<MasonryGallery.Infrastructure.Video.FfmpegThumbnailGenerator>.Instance)
            .GenerateThumbnailAsync(new ThumbnailJob(1, clip, MediaKind.Video, 1, DurationMs: 2000), 320);

        r.Error.Should().BeNull();
        System.Text.Encoding.ASCII.GetString(r.Value!.Webp, 8, 4).Should().Be("WEBP");
        using var codec = SKCodec.Create(new MemoryStream(r.Value.Webp));
        (codec.Info.Width, codec.Info.Height).Should().Be((180, 320));
    }

    [Fact]
    public async Task Setting_a_new_tag_creates_it()
    {
        var rig = await SetupAsync();
        var id = (await new GetLayoutItems(rig.Media).ExecuteAsync())[0].Id;
        await rig.Media.SetTagsAsync(id, ["holiday"]);
        (await rig.Media.GetTagsForMediaAsync(id)).Select(t => t.Name).Should().Equal("holiday");
    }
}
