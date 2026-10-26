namespace MasonryGallery.Benchmarks;

using System.Diagnostics;
using Dapper;
using MasonryGallery.Application.Pipeline;
using MasonryGallery.Application.Ports;
using MasonryGallery.Application.UseCases;
using MasonryGallery.Domain;
using MasonryGallery.Infrastructure.FileSystem;
using MasonryGallery.Infrastructure.Imaging;
using MasonryGallery.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The design §2 budgets that can be measured without a window, on a library made by <see cref="Dataset"/>.
/// A budget missed by more than 20% fails the run (exit code 1), as §12 asks of CI; a smaller miss is reported.
/// Scroll smoothness, start-to-first-paint and RAM need the real UI and are not measured here.
/// </summary>
public static class Budgets
{
    private const double Tolerance = 1.2;

    /// <param name="Target">Null for figures the design sets no budget for; they are reported, never failed.</param>
    private sealed record Row(string Metric, double Value, string Unit, double? Target, bool HigherIsBetter = false)
    {
        public double Ratio => Target is not { } t ? 0 : HigherIsBetter ? t / Math.Max(Value, 1e-9) : Value / t;
        public string Status => Target is null ? "info" : Ratio <= 1 ? "ok" : Ratio <= Tolerance ? "slow" : "FAIL";
    }

    public static async Task<int> RunAsync(string root)
    {
        if (!Directory.Exists(Dataset.Stubs(root)))
        {
            Console.Error.WriteLine($"No dataset in {root}; run 'generate {root}' first.");
            return 2;
        }
        var rows = new List<Row>();

        var dbPath = Path.Combine(root, "budget.db");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            File.Delete(dbPath + suffix);
        var factory = new SqliteConnectionFactory(dbPath, NullLogger<SqliteConnectionFactory>.Instance);
        await SchemaMigrator.MigrateAsync(factory, NullLogger.Instance);
        var libs = new SqliteLibraryRepository(factory, NullLogger<SqliteLibraryRepository>.Instance);
        var media = new SqliteMediaRepository(factory, NullLogger<SqliteMediaRepository>.Instance);
        // Thumbnails are measured on their own below; here they would only time the stub images.
        var scan = new Scan(libs, media, new FileSystemEnumerator(NullLogger<FileSystemEnumerator>.Instance),
            new SkiaProbe(NullLogger<SkiaProbe>.Instance), new NoThumbnails(), new NoStore(), new SystemClock(),
            new PipelineProgressReporter(), NullLogger<Scan>.Instance);
        var library = (await new AddLibrary(libs).ExecuteAsync("Budget", Dataset.Stubs(root))).Value!;

        // Initial scan. The budget is for enumeration: every file listed in the gallery (as a placeholder until
        // probed), with the first ones showing early. Reading each file's header (size, EXIF date) follows behind.
        var expected = Directory.EnumerateFiles(Dataset.Stubs(root), "*.jpg", SearchOption.AllDirectories).Count();
        TimeSpan firstItems = TimeSpan.Zero, allListed = TimeSpan.Zero;
        var sw = Stopwatch.StartNew();
        var scanning = scan.ExecuteAsync(library.Id);
        using (var conn = factory.CreateConnection())
        {
            while (!scanning.IsCompleted && allListed == TimeSpan.Zero)
            {
                var listed = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Media");
                if (listed > 0 && firstItems == TimeSpan.Zero) firstItems = sw.Elapsed;
                if (listed >= expected) allListed = sw.Elapsed;
                await Task.Delay(20);
            }
        }
        Require(await scanning, "initial scan");
        var initial = sw.Elapsed;
        if (allListed == TimeSpan.Zero) allListed = initial;
        var files = await media.CountLiveAsync(library.Id.Value);
        var scale = files / 100_000.0;
        rows.Add(new Row("First items in the gallery", firstItems.TotalSeconds, "s", 1));
        rows.Add(new Row($"All {files:N0} files listed (enumeration)", allListed.TotalSeconds, "s", 10 * scale));
        rows.Add(new Row("Initial scan incl. reading every header", initial.TotalSeconds, "s", null));

        sw.Restart();
        Require(await scan.ExecuteAsync(library.Id), "rescan");
        rows.Add(new Row("Rescan with no changes", sw.Elapsed.TotalSeconds, "s", 5 * files / 100_000.0));

        // The query behind the first paint: every layout record, newest first.
        await media.GetLayoutItemsAsync(new MediaFilter());
        sw.Restart();
        var layout = await media.GetLayoutItemsAsync(new MediaFilter());
        rows.Add(new Row($"Gallery layout query ({layout.Count:N0} items)", sw.Elapsed.TotalMilliseconds, "ms", 1000));

        // A 200-item page, from the start and from deep in the keyset.
        var (page, _) = await media.QueryPageAsync(200, null, null, null, null, null, null);
        var timings = new List<double>();
        for (var i = 0; i < 50; i++)
        {
            var cursor = i % 2 == 0 ? null : page.LastOrDefault();
            sw.Restart();
            (page, _) = await media.QueryPageAsync(200, cursor?.DateTaken, cursor?.Id, null, null, null, null);
            timings.Add(sw.Elapsed.TotalMilliseconds);
        }
        timings.Sort();
        rows.Add(new Row("Query page of 200 items (median)", timings[timings.Count / 2], "ms", 20));

        // Thumbnails: 12 MP JPEGs with the real generator, on 4 workers as the budget's reference machine has.
        var photos = Directory.GetFiles(Dataset.PhotosDir(root), "*.jpg");
        var workers = Math.Min(4, Environment.ProcessorCount);
        var generator = new SkiaThumbnailGenerator(NullLogger<SkiaThumbnailGenerator>.Instance);
        var failed = 0;
        sw.Restart();
        await Parallel.ForEachAsync(photos, new ParallelOptions { MaxDegreeOfParallelism = workers }, async (path, ct) =>
        {
            var r = await generator.GenerateSmallAsync(new ThumbnailJob(0, path, MediaKind.Image, ThumbnailFormat.Version), ct);
            if (!r.IsSuccess) Interlocked.Increment(ref failed);
        });
        var perSecond = photos.Length / sw.Elapsed.TotalSeconds;
        rows.Add(new Row($"Thumbnails of 12 MP photos, {workers} workers", perSecond, "/s", 150 * workers / 4.0, HigherIsBetter: true));

        Print(rows);
        Console.WriteLine("Not measured here (need the UI): scroll fps, start-to-first-paint, steady-state RAM.");
        if (failed > 0)
        {
            Console.Error.WriteLine($"{failed} thumbnails failed.");
            return 1;
        }
        return rows.Any(r => r.Status == "FAIL") ? 1 : 0;
    }

    private static void Print(List<Row> rows)
    {
        Console.WriteLine($"{"Budget",-46} {"Measured",12} {"Target",12}  Status");
        foreach (var r in rows)
        {
            var target = r.Target is { } t ? (r.HigherIsBetter ? ">= " : "<= ") + t.ToString(t >= 100 ? "N0" : "0.##") + " " + r.Unit : "-";
            Console.WriteLine($"{r.Metric,-46} {r.Value.ToString(r.Value >= 100 ? "N0" : "0.###") + " " + r.Unit,12} {target,12}  {r.Status}");
        }
    }

    private static void Require(Result result, string what)
    {
        if (!result.IsSuccess) throw new InvalidOperationException($"{what} failed: {result.Error}");
    }

    private sealed class NoThumbnails : IThumbnailGenerator
    {
        public ValueTask<Result<ThumbnailImage>> GenerateSmallAsync(ThumbnailJob job, CancellationToken ct = default) => new(new ThumbnailImage([0], 0));
        public ValueTask<Result<ThumbnailImage>> GenerateMediumAsync(ThumbnailJob job, CancellationToken ct = default) => new(new ThumbnailImage([0], 0));
    }

    private sealed class NoStore : IThumbnailStore
    {
        public Task<ReadOnlyMemory<byte>?> GetAsync(int mediaId, int size, CancellationToken ct = default) => Task.FromResult<ReadOnlyMemory<byte>?>(null);
        public Task SaveAsync(int mediaId, int size, ReadOnlyMemory<byte> data, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(int mediaId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> CleanOrphansAsync(IReadOnlySet<int> knownIds, DateTime olderThanUtc, CancellationToken ct = default) => Task.FromResult(0);
        public Task<CacheUsage> GetUsageAsync(int size, CancellationToken ct = default) => Task.FromResult(new CacheUsage(0, 0));
        public Task<CacheUsage> TrimAsync(int size, long maxBytes, CancellationToken ct = default) => Task.FromResult(new CacheUsage(0, 0));
        public Task DeleteStaleVersionsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
