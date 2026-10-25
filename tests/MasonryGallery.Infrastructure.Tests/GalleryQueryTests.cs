namespace MasonryGallery.Infrastructure.Tests;

using FluentAssertions;
using MasonryGallery.Application.Ports;
using MasonryGallery.Application.UseCases;
using MasonryGallery.Domain;
using MasonryGallery.Infrastructure.Persistence;
using MasonryGallery.Infrastructure.Thumbnails;
using MasonryGallery.Infrastructure.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class GalleryQueryTests : IDisposable
{
    private static readonly DateTime Jan = new(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Directory.CreateTempSubdirectory("mg-query-").FullName;
    private SqliteMediaRepository _media = null!;
    private SqliteLibraryRepository _libs = null!;
    private LibraryId _photos, _other;

    public void Dispose() => Directory.Delete(_dir, true);

    // photos: beach.jpg (Jan, image), cat.png (Feb, image), party.mp4 (Mar, video)   other: zebra.jpg (Apr)
    private async Task SeedAsync()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_dir, "t.db"), NullLogger<SqliteConnectionFactory>.Instance);
        await SchemaMigrator.MigrateAsync(factory, NullLogger.Instance);
        _libs = new SqliteLibraryRepository(factory, NullLogger<SqliteLibraryRepository>.Instance);
        _media = new SqliteMediaRepository(factory, NullLogger<SqliteMediaRepository>.Instance);
        _photos = (await _libs.AddAsync(new Library("Photos", "/photos"))).Value!.Id;
        _other = (await _libs.AddAsync(new Library("Other", "/other"))).Value!.Id;

        MediaItem Item(LibraryId lib, string name, int month, MediaKind kind = MediaKind.Image) => new()
        {
            LibraryId = lib, RelPath = "2024/" + name, FileName = name, Kind = kind, SizeBytes = 10,
            ModifiedUtc = Jan, DateTaken = Jan.AddMonths(month), DateAdded = Jan.AddMonths(-month),
            Width = 4, Height = 3, ProbeState = ProbeState.Done, ScanId = 1
        };
        await _media.UpsertBatchAsync([
            Item(_photos, "beach.jpg", 0), Item(_photos, "cat.png", 1), Item(_photos, "party.mp4", 2, MediaKind.Video),
            Item(_other, "zebra.jpg", 3)]);
    }

    private async Task<string[]> NamesAsync(MediaFilter filter)
    {
        var items = await _media.GetLayoutItemsAsync(filter);
        var names = await _media.GetFileNamesAsync(items.Select(i => i.Id).ToList());
        return items.Select(i => names[i.Id]).ToArray();
    }

    private async Task<int> IdOfAsync(string name)
        => (await _media.GetLayoutItemsAsync(new MediaFilter(Search: Path.GetFileNameWithoutExtension(name)))).Single().Id;

    [Fact]
    public async Task Filters_narrow_the_gallery()
    {
        await SeedAsync();

        (await NamesAsync(new MediaFilter())).Should().Equal("zebra.jpg", "party.mp4", "cat.png", "beach.jpg");
        (await NamesAsync(new MediaFilter(Kind: MediaKind.Video))).Should().Equal("party.mp4");
        (await NamesAsync(new MediaFilter(LibraryId: _other))).Should().Equal("zebra.jpg");
        (await NamesAsync(new MediaFilter(Search: "be"))).Should().Equal("beach.jpg");
        (await NamesAsync(new MediaFilter(Search: "2024"))).Should().HaveCount(4, "the folder path is searchable too");
        (await NamesAsync(new MediaFilter(From: Jan.AddMonths(1), To: Jan.AddMonths(2)))).Should().Equal("party.mp4", "cat.png");
    }

    [Theory]
    [InlineData(MediaSort.NewestFirst, new[] { "zebra.jpg", "party.mp4", "cat.png", "beach.jpg" })]
    [InlineData(MediaSort.OldestFirst, new[] { "beach.jpg", "cat.png", "party.mp4", "zebra.jpg" })]
    [InlineData(MediaSort.RecentlyAdded, new[] { "beach.jpg", "cat.png", "party.mp4", "zebra.jpg" })]
    [InlineData(MediaSort.Name, new[] { "beach.jpg", "cat.png", "party.mp4", "zebra.jpg" })]
    public async Task Sort_orders_the_gallery(MediaSort sort, string[] expected)
    {
        await SeedAsync();
        (await NamesAsync(new MediaFilter(Sort: sort))).Should().Equal(expected);
    }

    [Fact]
    public async Task Favorites_are_flagged_and_filterable()
    {
        await SeedAsync();
        var cat = await IdOfAsync("cat.png");

        await _media.SetFavoriteAsync(cat, true);

        (await NamesAsync(new MediaFilter(FavoritesOnly: true))).Should().Equal("cat.png");
        (await _media.GetLayoutItemsAsync(new MediaFilter())).Single(i => i.Id == cat).Favorite.Should().BeTrue();

        await _media.SetFavoriteAsync(cat, false);
        (await NamesAsync(new MediaFilter(FavoritesOnly: true))).Should().BeEmpty();
    }

    [Fact]
    public async Task Tags_filter_count_and_clean_up()
    {
        await SeedAsync();
        var beach = await IdOfAsync("beach.jpg");
        var cat = await IdOfAsync("cat.png");

        await _media.SetTagsAsync(beach, ["Summer", "summer ", "  ", "trip"]); // duplicate by case, blank
        await _media.SetTagsAsync(cat, ["SUMMER"]);

        (await _media.GetTagsForMediaAsync(beach)).Select(t => t.Name).Should().BeEquivalentTo(["Summer", "trip"]);
        (await _media.ListTagsAsync()).Should().Equal(new TagCount("Summer", 2), new TagCount("trip", 1));
        (await NamesAsync(new MediaFilter(Tag: "summer"))).Should().Equal("cat.png", "beach.jpg");

        await _media.SetTagsAsync(beach, ["Summer"]);
        (await _media.ListTagsAsync()).Select(t => t.Name).Should().Equal(["Summer"], "unused tags are dropped");
    }

    [Fact]
    public async Task Disabled_library_is_hidden()
    {
        await SeedAsync();
        var other = (await _libs.GetByIdAsync(_other)).Value!;
        await _libs.UpdateAsync(new Library(other.Name, other.RootPath) { Id = other.Id, Enabled = false });

        (await NamesAsync(new MediaFilter())).Should().NotContain("zebra.jpg");
    }

    [Fact]
    public async Task Failed_items_are_listed_with_their_stage()
    {
        await SeedAsync();
        var cat = await IdOfAsync("cat.png");
        var party = await IdOfAsync("party");
        await _media.UpdateThumbAsync(cat, ThumbState.Failed, 2);
        var broken = (await _media.GetByIdAsync(party))!;
        await _media.UpsertBatchAsync([new MediaItem
        {
            LibraryId = broken.LibraryId, RelPath = broken.RelPath, FileName = broken.FileName, Kind = broken.Kind,
            SizeBytes = broken.SizeBytes, ModifiedUtc = broken.ModifiedUtc, ProbeState = ProbeState.Failed, ScanId = 1
        }]);

        (await _media.CountFailedAsync()).Should().Be(2);
        (await _media.GetFailedAsync(10)).Select(f => (f.FileName, f.Stage, f.LibraryName))
            .Should().BeEquivalentTo([("cat.png", "thumbnail", "Photos"), ("party.mp4", "probe", "Photos")]);
    }

    [Fact]
    public async Task Removing_a_library_drops_its_rows_search_entries_and_thumbnails()
    {
        await SeedAsync();
        var store = new ShardedThumbnailStore(Path.Combine(_dir, "thumbs"), "v2", NullLogger<ShardedThumbnailStore>.Instance);
        var zebra = await IdOfAsync("zebra.jpg");
        var beach = await IdOfAsync("beach.jpg");
        await store.SaveAsync(zebra, 320, new byte[] { 1 });
        await store.SaveAsync(beach, 320, new byte[] { 1 });
        var clock = new FixedClock(DateTime.UtcNow.AddMinutes(1));

        var result = await new RemoveLibrary(_libs, _media, store, clock).ExecuteAsync(_other);

        result.Error.Should().BeNull();
        (await _libs.ListAsync()).Select(l => l.Name).Should().Equal("Photos");
        (await NamesAsync(new MediaFilter(Search: "zebra"))).Should().BeEmpty();
        File.Exists(store.GetPath(zebra, 320)).Should().BeFalse();
        File.Exists(store.GetPath(beach, 320)).Should().BeTrue();
    }

    [Fact]
    public async Task Library_being_scanned_cannot_be_removed()
    {
        await SeedAsync();
        await _libs.SetStatusAsync(_other, LibraryStatus.Scanning);
        var store = new ShardedThumbnailStore(Path.Combine(_dir, "thumbs"), "v2", NullLogger<ShardedThumbnailStore>.Instance);

        (await new RemoveLibrary(_libs, _media, store, new FixedClock(DateTime.UtcNow)).ExecuteAsync(_other))
            .IsSuccess.Should().BeFalse();
        (await _libs.ListAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task Playable_flag_is_stored_and_kept_across_scans()
    {
        await SeedAsync();
        var party = (await _media.GetByIdAsync(await IdOfAsync("party")))!;
        party.Playable = false;
        await _media.UpsertBatchAsync([party]);
        party.Playable = null; // a rescan row carries no probe data
        await _media.UpsertBatchAsync([party]);

        (await _media.GetByIdAsync(party.Id))!.Playable.Should().BeFalse();
    }

    [Fact]
    public async Task Ffprobe_reads_display_matrix_rotation()
    {
        var ffmpeg = FfmpegLocator.Locate();
        var ffprobe = FfmpegLocator.LocateProbe();
        if (ffmpeg is null || ffprobe is null) return; // no ffmpeg on this machine

        var plain = Path.Combine(_dir, "plain.mp4");
        var rotated = Path.Combine(_dir, "rotated.mp4");
        await RunAsync(ffmpeg, "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=320x240:duration=1", "-c:v", "libx264", plain);
        await RunAsync(ffmpeg, "-loglevel", "error", "-display_rotation", "90", "-i", plain, "-c", "copy", rotated);

        var r = await new FfprobeProbe(ffprobe, NullLogger<FfprobeProbe>.Instance).ProbeAsync(rotated, MediaKind.Video);

        r.Error.Should().BeNull();
        (r.Value!.Width, r.Value.Height, r.Value.NativePlayable).Should().Be((240, 320, true));
    }

    private static async Task RunAsync(string exe, params string[] args)
    {
        using var p = System.Diagnostics.Process.Start(exe, args);
        await p.WaitForExitAsync();
        p.ExitCode.Should().Be(0);
    }

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime UtcNow => now;
        public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.CompletedTask;
    }
}
