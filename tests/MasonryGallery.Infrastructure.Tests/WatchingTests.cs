namespace MasonryGallery.Infrastructure.Tests;

using System.Collections.Concurrent;
using FluentAssertions;
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
using Xunit;

public sealed class WatchingTests : IDisposable
{
    private static readonly WatcherTiming Fast = new(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(600));
    private readonly string _dir = Directory.CreateTempSubdirectory("mg-watch-").FullName;
    private string Lib => Path.Combine(_dir, "lib");

    public WatchingTests() => Directory.CreateDirectory(Path.Combine(_dir, "lib"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>Writes a PNG; by default dated a minute ago so targeted scans don't treat it as still being written.</summary>
    private string WritePng(string rel, bool fresh = false)
    {
        var path = Path.Combine(Lib, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bmp = new SKBitmap(40, 30);
        bmp.Erase(SKColors.SeaGreen);
        File.WriteAllBytes(path, SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100).ToArray());
        if (!fresh)
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        return path;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met in time");
            await Task.Delay(50);
        }
    }

    // ── Watcher ──

    private (IFileWatcher Watcher, ConcurrentQueue<WatchSignal> Signals) Watch()
    {
        var result = new FileWatcherFactory(NullLoggerFactory.Instance, Fast).TryWatch(Lib);
        result.Error.Should().BeNull();
        var signals = new ConcurrentQueue<WatchSignal>();
        result.Value!.Signals.Subscribe(signals.Enqueue);
        return (result.Value, signals);
    }

    private static HashSet<string> ChangedPaths(ConcurrentQueue<WatchSignal> signals)
        => signals.OfType<WatchSignal.Changed>().SelectMany(c => c.Paths).ToHashSet();

    [Fact]
    public async Task Watcher_batches_a_burst_of_changes()
    {
        var (watcher, signals) = Watch();
        using var _ = watcher;

        for (var i = 0; i < 50; i++)
            File.WriteAllText(Path.Combine(Lib, $"f{i}.jpg"), "x");

        await WaitUntilAsync(() => Task.FromResult(ChangedPaths(signals).Count >= 50));
        ChangedPaths(signals).Should().BeEquivalentTo(Enumerable.Range(0, 50).Select(i => $"f{i}.jpg"));
        signals.Count.Should().BeLessThan(10, "50 writes arrive as a few batches, not 50+ events");
    }

    [Fact]
    public async Task Watcher_reports_both_sides_of_a_rename_and_folder_deletes()
    {
        Directory.CreateDirectory(Path.Combine(Lib, "album"));
        File.WriteAllText(Path.Combine(Lib, "album", "a.jpg"), "x");
        File.WriteAllText(Path.Combine(Lib, "old.jpg"), "x");
        var (watcher, signals) = Watch();
        using var _ = watcher;

        File.Move(Path.Combine(Lib, "old.jpg"), Path.Combine(Lib, "new.jpg"));
        Directory.Delete(Path.Combine(Lib, "album"), recursive: true);

        await WaitUntilAsync(() => Task.FromResult(ChangedPaths(signals).IsSupersetOf(["old.jpg", "new.jpg", "album"])));
    }

    [Fact]
    public void Watcher_refuses_a_missing_folder()
        => new FileWatcherFactory(NullLoggerFactory.Instance).TryWatch(Path.Combine(_dir, "nope")).IsSuccess.Should().BeFalse();

    [Fact]
    public async Task Batcher_flushes_a_constant_stream_at_the_max_delay()
    {
        var batches = new ConcurrentQueue<IReadOnlyCollection<string>>();
        using var batcher = new ChangeBatcher(new WatcherTiming(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300)), batches.Enqueue);

        // Never quiet for 200 ms, so only the 300 ms deadline can flush.
        for (var i = 0; i < 20; i++)
        {
            batcher.Add($"p{i}");
            await Task.Delay(50);
        }

        batches.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    // ── Targeted rescans ──

    private sealed record Rig(Scan Scan, SqliteMediaRepository Media, SqliteLibraryRepository Libs, LibraryId Id, PipelineProgressReporter Progress)
    {
        public async Task<string[]> NamesAsync()
        {
            var items = await Media.GetLayoutItemsAsync(new MediaFilter(Sort: MediaSort.Name));
            var names = await Media.GetFileNamesAsync(items.Select(i => i.Id).ToList());
            return items.Select(i => names[i.Id]).ToArray();
        }
    }

    private async Task<Rig> SetupAsync()
    {
        WritePng("a.png");
        WritePng("sub/b.png");
        var factory = new SqliteConnectionFactory(Path.Combine(_dir, "t.db"), NullLogger<SqliteConnectionFactory>.Instance);
        await SchemaMigrator.MigrateAsync(factory, NullLogger.Instance);
        var libs = new SqliteLibraryRepository(factory, NullLogger<SqliteLibraryRepository>.Instance);
        var media = new SqliteMediaRepository(factory, NullLogger<SqliteMediaRepository>.Instance);
        var progress = new PipelineProgressReporter();
        var scan = new Scan(libs, media, new FileSystemEnumerator(NullLogger<FileSystemEnumerator>.Instance),
            new SkiaProbe(NullLogger<SkiaProbe>.Instance), new SkiaThumbnailGenerator(NullLogger<SkiaThumbnailGenerator>.Instance),
            new ShardedThumbnailStore(Path.Combine(_dir, "thumbs"), "v2", NullLogger<ShardedThumbnailStore>.Instance),
            new SystemClock(), progress, NullLogger<Scan>.Instance);
        var id = (await new AddLibrary(libs).ExecuteAsync("Lib", Lib)).Value!.Id;
        (await scan.ExecuteAsync(id)).Error.Should().BeNull();
        return new Rig(scan, media, libs, id, progress);
    }

    [Fact]
    public async Task Targeted_scan_indexes_new_files_and_folders()
    {
        var rig = await SetupAsync();
        WritePng("c.png");
        WritePng("trip/d.png");
        WritePng("trip/day2/e.png");

        var r = await rig.Scan.ExecutePathsAsync(rig.Id, ["c.png", "trip", "trip/d.png"]);

        r.Value.Should().BeEmpty();
        (await rig.NamesAsync()).Should().Equal("a.png", "b.png", "c.png", "d.png", "e.png");
    }

    [Fact]
    public async Task Targeted_scan_removes_deleted_files_and_folders()
    {
        var rig = await SetupAsync();
        File.Delete(Path.Combine(Lib, "a.png"));
        Directory.Delete(Path.Combine(Lib, "sub"), true);

        await rig.Scan.ExecutePathsAsync(rig.Id, ["a.png", "sub"]);

        (await rig.NamesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Nomedia_hides_its_folder_and_removing_it_brings_it_back()
    {
        var rig = await SetupAsync();
        var marker = Path.Combine(Lib, "sub", ".nomedia");

        File.WriteAllText(marker, "");
        await rig.Scan.ExecutePathsAsync(rig.Id, ["sub/.nomedia"]);
        (await rig.NamesAsync()).Should().Equal("a.png");

        File.Delete(marker);
        await rig.Scan.ExecutePathsAsync(rig.Id, ["sub/.nomedia"]);
        (await rig.NamesAsync()).Should().Equal("a.png", "b.png");
    }

    [Fact]
    public async Task File_still_being_written_is_deferred()
    {
        var rig = await SetupAsync();
        WritePng("copying.png", fresh: true);

        var r = await rig.Scan.ExecutePathsAsync(rig.Id, ["copying.png"]);

        r.Value.Should().Equal("copying.png");
        (await rig.NamesAsync()).Should().NotContain("copying.png");
    }

    [Fact]
    public async Task Rows_from_targeted_scans_survive_the_next_full_scan_only_while_on_disk()
    {
        var rig = await SetupAsync();
        WritePng("c.png");
        await rig.Scan.ExecutePathsAsync(rig.Id, ["c.png"]);

        (await rig.Scan.ExecuteAsync(rig.Id)).Error.Should().BeNull();
        (await rig.NamesAsync()).Should().Equal("a.png", "b.png", "c.png");

        File.Delete(Path.Combine(Lib, "c.png")); // no watcher event: the full scan must still sweep it
        (await rig.Scan.ExecuteAsync(rig.Id)).Error.Should().BeNull();
        (await rig.NamesAsync()).Should().Equal("a.png", "b.png");
    }

    [Theory]
    [InlineData("a.png", PathKind.File)]
    [InlineData("sub", PathKind.Directory)]
    [InlineData("gone.png", PathKind.Missing)]
    [InlineData("notes.txt", PathKind.Ignored)]
    [InlineData(".hidden/x.png", PathKind.Ignored)]
    [InlineData("@eaDir/x.png", PathKind.Ignored)]
    [InlineData("quiet/x.png", PathKind.Ignored)]
    [InlineData("quiet", PathKind.Ignored)]
    [InlineData("link/x.png", PathKind.Ignored)]
    public void Inspect_applies_the_enumeration_rules(string rel, PathKind expected)
    {
        WritePng("a.png");
        Directory.CreateDirectory(Path.Combine(Lib, "sub"));
        File.WriteAllText(Path.Combine(Lib, "notes.txt"), "");
        WritePng(".hidden/x.png");
        WritePng("@eaDir/x.png");
        WritePng("quiet/x.png");
        File.WriteAllText(Path.Combine(Lib, "quiet", ".nomedia"), "");
        WritePng(Path.Combine("..", "outside", "x.png"));
        Directory.CreateSymbolicLink(Path.Combine(Lib, "link"), Path.Combine(_dir, "outside"));

        new FileSystemEnumerator(NullLogger<FileSystemEnumerator>.Instance)
            .Inspect(Lib, rel, followSymlinks: false).Kind.Should().Be(expected);
    }

    // ── Phase 4 exit criterion: add or remove files while running and the gallery follows ──

    [Fact]
    public async Task Live_changes_reach_the_gallery()
    {
        var rig = await SetupAsync();
        var changes = new ConcurrentQueue<GalleryChange>();
        using var __ = rig.Progress.Changes.Subscribe(changes.Enqueue);
        using var scheduler = new ScanScheduler(rig.Scan, new SystemClock(), NullLogger<ScanScheduler>.Instance);
        using var monitor = new LibraryMonitor(rig.Libs, new FileWatcherFactory(NullLoggerFactory.Instance, Fast),
            new FileSystemEnumerator(NullLogger<FileSystemEnumerator>.Instance), scheduler, new SystemClock(),
            NullLogger<LibraryMonitor>.Instance);
        await monitor.StartAsync();
        await scheduler.WhenIdleAsync();
        monitor.Statuses[rig.Id].Mode.Should().Be(WatchMode.Live);

        WritePng("added.png");
        await WaitUntilAsync(async () => (await rig.NamesAsync()).Contains("added.png"));

        WritePng("copying.png", fresh: true); // deferred once, then picked up after it settles
        await WaitUntilAsync(async () => (await rig.NamesAsync()).Contains("copying.png"));

        File.Delete(Path.Combine(Lib, "a.png"));
        Directory.Delete(Path.Combine(Lib, "sub"), true);
        await WaitUntilAsync(async () => (await rig.NamesAsync()).SequenceEqual(["added.png", "copying.png"]));

        await WaitUntilAsync(() => Task.FromResult(changes.Any(c => c.ItemsChanged)));
    }
}
