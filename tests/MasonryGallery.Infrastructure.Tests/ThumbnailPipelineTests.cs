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

public sealed class ThumbnailPipelineTests : IDisposable
{
    private static readonly DateTime Base = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Directory.CreateTempSubdirectory("mg-pipe-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>Records generation order and attempts; <paramref name="fail"/> decides per (id, attempt).</summary>
    private sealed class FakeGenerator(Func<int, int, bool> fail, Action<int>? onCall = null) : IThumbnailGenerator
    {
        public readonly ConcurrentQueue<int> Generated = new();
        public readonly ConcurrentDictionary<int, int> Attempts = new();

        public ValueTask<Result<ThumbnailImage>> GenerateSmallAsync(ThumbnailJob job, CancellationToken ct = default)
        {
            var attempt = Attempts.AddOrUpdate(job.MediaId, 1, (_, n) => n + 1);
            onCall?.Invoke(job.MediaId);
            if (fail(job.MediaId, attempt))
                return ValueTask.FromResult(Result<ThumbnailImage>.Failure("boom"));
            Generated.Enqueue(job.MediaId);
            return ValueTask.FromResult<Result<ThumbnailImage>>(new ThumbnailImage([1], 0x123456));
        }

        public ValueTask<Result<ThumbnailImage>> GenerateMediumAsync(ThumbnailJob job, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed record Rig(Library Library, SqliteMediaRepository Media, SqliteLibraryRepository Libs, ShardedThumbnailStore Store)
    {
        /// <summary>IDs ordered oldest file first (file i has mtime Base + i minutes).</summary>
        public async Task<int[]> IdsOldestFirstAsync()
        {
            var (items, _) = await Media.QueryPageAsync(100, null, null, null, null, null, null);
            return items.OrderBy(i => i.DateTaken).Select(i => i.Id).ToArray();
        }
    }

    // Indexes `count` PNGs with real Skia, then re-queues every thumbnail for the pipeline under test.
    private async Task<Rig> SetupAsync(int count)
    {
        var lib = Path.Combine(_dir, "lib");
        Directory.CreateDirectory(lib);
        for (var i = 0; i < count; i++)
        {
            using var bmp = new SKBitmap(40, 30);
            bmp.Erase(SKColors.OrangeRed);
            var path = Path.Combine(lib, $"{i}.png");
            File.WriteAllBytes(path, SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100).ToArray());
            File.SetLastWriteTimeUtc(path, Base.AddMinutes(i));
        }

        var factory = new SqliteConnectionFactory(Path.Combine(_dir, "t.db"), NullLogger<SqliteConnectionFactory>.Instance);
        await SchemaMigrator.MigrateAsync(factory, NullLogger.Instance);
        var libs = new SqliteLibraryRepository(factory, NullLogger<SqliteLibraryRepository>.Instance);
        var media = new SqliteMediaRepository(factory, NullLogger<SqliteMediaRepository>.Instance);
        var store = new ShardedThumbnailStore(Path.Combine(_dir, "thumbs"), "v2", NullLogger<ShardedThumbnailStore>.Instance);
        var library = (await new AddLibrary(libs).ExecuteAsync("Lib", lib)).Value!;
        await new Scan(libs, media, new FileSystemEnumerator(NullLogger<FileSystemEnumerator>.Instance),
            new SkiaProbe(NullLogger<SkiaProbe>.Instance), new SkiaThumbnailGenerator(NullLogger<SkiaThumbnailGenerator>.Instance),
            store, new SystemClock(), new PipelineProgressReporter(), NullLogger<Scan>.Instance).ExecuteAsync(library.Id);

        await media.ResetOutdatedThumbnailsAsync(library.Id.Value, int.MaxValue);
        return new Rig(library, media, libs, store);
    }

    private static Task RunAsync(Rig rig, IThumbnailGenerator generator, ViewportHint viewport, CancellationToken ct = default)
        => new IngestPipeline(NullLogger.Instance).RunAsync(rig.Library,
            new FileSystemEnumerator(NullLogger<FileSystemEnumerator>.Instance).EnumerateAsync(rig.Library.RootPath, false, ct), rig.Media,
            new SkiaProbe(NullLogger<SkiaProbe>.Instance), generator, rig.Store, scanId: 99,
            new IngestOptions(Workers: 1, VideoWorkers: 1, viewport), new PipelineProgressReporter(), ct);

    [Fact]
    public async Task On_screen_items_go_first_then_newest_first()
    {
        var rig = await SetupAsync(6);
        var ids = await rig.IdsOldestFirstAsync();
        var viewport = new ViewportHint();
        viewport.Set([ids[0], ids[1]]); // the two oldest, which the normal lane would do last

        var generator = new FakeGenerator((_, _) => false);
        await RunAsync(rig, generator, viewport);

        var order = generator.Generated.ToArray();
        order[..2].Should().BeEquivalentTo([ids[0], ids[1]]);
        order[2..].Should().Equal(ids[2..].Reverse());

        var layout = await new GetLayoutItems(rig.Media).ExecuteAsync();
        layout.Should().OnlyContain(i => i.ThumbReady && PlaceholderColor.FromBytes(i.Placeholder) == 0x123456);
    }

    [Fact]
    public async Task Transient_failures_are_retried_and_persistent_ones_marked_failed()
    {
        var rig = await SetupAsync(2);
        var ids = await rig.IdsOldestFirstAsync();
        var broken = ids[0];
        // Every file fails twice; the broken one never succeeds.
        var generator = new FakeGenerator((id, attempt) => id == broken || attempt <= 2);

        await RunAsync(rig, generator, new ViewportHint());

        generator.Attempts.Should().BeEquivalentTo(new Dictionary<int, int> { [ids[0]] = 3, [ids[1]] = 3 });
        (await rig.Media.GetByIdAsync(ids[1]))!.ThumbState.Should().Be(ThumbState.Done);
        (await rig.Media.GetByIdAsync(broken))!.ThumbState.Should().Be(ThumbState.Failed);
        (await rig.Media.GetByIdAsync(broken))!.ThumbVersion.Should().Be(ThumbnailFormat.Version, "a new generator version retries it");
    }

    [Fact]
    public async Task Cancelled_job_stays_pending_for_the_next_run()
    {
        var rig = await SetupAsync(3);
        using var cts = new CancellationTokenSource();
        var generator = new FakeGenerator((_, _) => true, onCall: _ => cts.Cancel());

        var run = () => RunAsync(rig, generator, new ViewportHint(), cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        (await rig.Media.CountPendingThumbnailsAsync(rig.Library.Id.Value)).Should().Be(3);
    }
}
