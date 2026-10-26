namespace MasonryGallery.Application.Pipeline;

using System.Threading.Channels;
using MasonryGallery.Application.Ports;
using MasonryGallery.Domain;
using Microsoft.Extensions.Logging;

/// <param name="Workers">Probe and thumbnail parallelism; kept low on spinning or network disks (design §5.5).</param>
/// <param name="VideoWorkers">Cap on concurrent ffprobe/ffmpeg processes.</param>
/// <param name="Viewport">On-screen IDs, served first by the thumbnail stage.</param>
/// <param name="Quality">WebP quality of generated thumbnails.</param>
/// <param name="Sidecars">Reads XMP sidecars during the probe stage; null skips them.</param>
/// <param name="Pause">The user's pause switch; probe and thumbnail workers wait on it before each item.</param>
public sealed record IngestOptions(int Workers, int VideoWorkers, ViewportHint Viewport, int Quality = ThumbnailFormat.DefaultQuality,
    ISidecarReader? Sidecars = null, IndexingPause? Pause = null);

public sealed class IngestPipeline : IDisposable
{
    private const int ChannelCapacity = 1024;
    private const int ProbeBatch = 100;
    private const int ThumbnailPage = 256;
    // Transient failures (file still being copied, NAS hiccup) get two more tries; short, so corrupt files fail fast.
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)];

    private readonly ILogger _logger;
    private readonly Channel<MediaItem> _toDiff;
    private readonly Channel<MediaItem> _toProbe;
    private int _seen;

    public IngestPipeline(ILogger logger)
    {
        _logger = logger;
        _toDiff = Channel.CreateBounded<MediaItem>(
            new BoundedChannelOptions(ChannelCapacity) { FullMode = BoundedChannelFullMode.Wait });
        _toProbe = Channel.CreateBounded<MediaItem>(
            new BoundedChannelOptions(ChannelCapacity) { FullMode = BoundedChannelFullMode.Wait });
    }

    /// <param name="files">What to index: the whole library for a full scan, or just the changed paths.</param>
    /// <returns>The number of media files taken from <paramref name="files"/>.</returns>
    public async Task<int> RunAsync(
        Library library,
        IAsyncEnumerable<FileEntry> files,
        IMediaRepository mediaRepository,
        IMediaProbe mediaProbe,
        IThumbnailGenerator thumbnailGenerator,
        IThumbnailStore thumbnailStore,
        int scanId,
        IngestOptions options,
        IProgressReporter progress,
        CancellationToken ct)
    {
        _logger.LogInformation("Starting ingest pipeline for library {LibraryId} ({Root}) with {Workers} workers",
            library.Id.Value, library.RootPath, options.Workers);

        progress.Report(new PipelineEvent("Indexing files", 0, 0));
        using (var stages = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            await Task.WhenAll(
                CancelOthersOnFault(RunEnumerationAsync(library, files, scanId, stages.Token), stages),
                CancelOthersOnFault(RunDiffAsync(library.Id.Value, mediaRepository, progress, stages.Token), stages),
                CancelOthersOnFault(RunProbeAsync(mediaProbe, mediaRepository, library, options, progress, stages.Token), stages));
        }
        await RunThumbnailsAsync(library, mediaRepository, thumbnailGenerator, thumbnailStore, options, progress, ct);
        _logger.LogInformation("Ingest pipeline completed for library {LibraryId}", library.Id.Value);
        return _seen;
    }

    // A stage blocked writing to a full channel would wait forever once its reader died, so one failure stops them all.
    private static async Task CancelOthersOnFault(Task stage, CancellationTokenSource stages)
    {
        try
        {
            await stage;
        }
        catch
        {
            await stages.CancelAsync();
            throw;
        }
    }

    private async Task RunEnumerationAsync(
        Library library,
        IAsyncEnumerable<FileEntry> files,
        int scanId,
        CancellationToken ct)
    {
        try
        {
            await foreach (var entry in files.WithCancellation(ct))
            {
                var ext = Path.GetExtension(entry.RelativePath);
                if (!IsMediaExtension(ext))
                    continue;

                var media = new MediaItem
                {
                    LibraryId = library.Id,
                    RelPath = NormalizePath(entry.RelativePath),
                    FileName = Path.GetFileName(entry.RelativePath),
                    Kind = IsVideoExtension(ext) ? MediaKind.Video : MediaKind.Image,
                    SizeBytes = entry.Size,
                    ModifiedUtc = entry.ModifiedUtc,
                    DateTaken = entry.ModifiedUtc,
                    ScanId = scanId,
                    SidecarPath = entry.SidecarPath is { } sidecar ? NormalizePath(sidecar) : null,
                    SidecarModifiedUtc = entry.SidecarModifiedUtc,
                    ProbeState = ProbeState.Pending,
                    ThumbState = ThumbState.Pending,
                    DateAdded = DateTime.UtcNow
                };

                _seen++;
                await _toDiff.Writer.WriteAsync(media, ct);
            }
        }
        finally
        {
            _toDiff.Writer.Complete();
        }
    }

    private async Task RunDiffAsync(
        int libraryId,
        IMediaRepository mediaRepository,
        IProgressReporter progress,
        CancellationToken ct)
    {
        var batch = new List<MediaItem>(5000);
        try
        {
            await foreach (var item in _toDiff.Reader.ReadAllAsync(ct))
            {
                batch.Add(item);
                if (batch.Count >= 5000)
                    await FlushDiffAsync(libraryId, batch, mediaRepository, progress, ct);
            }

            await FlushDiffAsync(libraryId, batch, mediaRepository, progress, ct);
        }
        finally
        {
            _toProbe.Writer.Complete();
        }
    }

    private async Task FlushDiffAsync(
        int libraryId, List<MediaItem> batch, IMediaRepository mediaRepository, IProgressReporter progress, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        var known = await mediaRepository.GetScanStatesAsync(libraryId, batch.ConvertAll(i => i.RelPath), ct);
        await mediaRepository.UpsertBatchAsync(batch, ct);
        var changed = false;
        foreach (var item in batch)
        {
            if (NeedsProbe(item, known.GetValueOrDefault(item.RelPath)))
            {
                changed = true;
                await _toProbe.Writer.WriteAsync(item, ct);
            }
        }
        // New rows show up in the grid right away, as placeholders, before their probe finishes.
        if (changed)
            progress.ItemsChanged();
        batch.Clear();
    }

    // Unchanged files keep their stored probe result, so a no-change rescan reads no file headers.
    // A new, edited or deleted sidecar is re-read by probing again; the thumbnail is left alone.
    private static bool NeedsProbe(MediaItem item, ScanState? known)
        => known is null
           || known.ProbeState == ProbeState.Pending
           || known.SizeBytes != item.SizeBytes
           || ToUnixMs(known.ModifiedUtc) != ToUnixMs(item.ModifiedUtc)
           || (known.SidecarModifiedUtc is { } was ? ToUnixMs(was) : (long?)null)
              != (item.SidecarModifiedUtc is { } now ? ToUnixMs(now) : (long?)null);

    // The DB stores milliseconds; file system timestamps carry sub-millisecond ticks.
    private static long ToUnixMs(DateTime utc)
        => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private async Task RunProbeAsync(
        IMediaProbe mediaProbe,
        IMediaRepository mediaRepository,
        Library library,
        IngestOptions options,
        IProgressReporter progress,
        CancellationToken ct)
    {
        var rootPath = library.RootPath;
        using var videoGate = new SemaphoreSlim(options.VideoWorkers);
        var batch = new List<(MediaItem Item, SidecarData? Sidecar)>(ProbeBatch);

        await Parallel.ForEachAsync(_toProbe.Reader.ReadAllAsync(ct),
            new ParallelOptions { MaxDegreeOfParallelism = options.Workers, CancellationToken = ct },
            async (item, token) =>
            {
                if (options.Pause is { } pause) await pause.WaitWhilePausedAsync(token);
                var metadata = await GatedAsync(item.Kind, videoGate,
                    () => mediaProbe.ProbeAsync(Path.Combine(rootPath, item.RelPath), item.Kind, token).AsTask(), token);

                var sidecar = await ReadSidecarAsync(options.Sidecars, rootPath, item, token);

                if (metadata.IsSuccess)
                {
                    var m = metadata.Value!;
                    item.Width = m.Width;
                    item.Height = m.Height;
                    item.DurationMs = m.DurationMs;
                    // The file's own capture date wins; the sidecar's fills in for files without one.
                    item.DateTaken = m.DateTaken ?? sidecar?.DateTaken;
                    item.Playable = item.Kind == MediaKind.Video ? m.NativePlayable : null;
                    item.ProbeState = ProbeState.Done;
                }
                else
                {
                    item.ProbeState = ProbeState.Failed;
                    item.ErrorCode = 1;
                }

                List<(MediaItem Item, SidecarData? Sidecar)>? full = null;
                lock (batch)
                {
                    batch.Add((item, sidecar));
                    if (batch.Count >= ProbeBatch)
                    {
                        full = [.. batch];
                        batch.Clear();
                    }
                }
                if (full is not null)
                {
                    await SaveProbedAsync(full, token);
                    progress.ItemsChanged(); // real aspect ratios replace the 1:1 placeholders
                }
            });

        if (batch.Count > 0)
        {
            await SaveProbedAsync(batch, ct);
            progress.ItemsChanged();
        }
        _logger.LogInformation("Probe stage completed");

        async Task SaveProbedAsync(List<(MediaItem Item, SidecarData? Sidecar)> probed, CancellationToken token)
        {
            await mediaRepository.UpsertBatchAsync(probed.Select(p => p.Item), token);
            // Every re-probed item: one whose sidecar disappeared or broke has its old tags cleared.
            if (options.Sidecars is not null)
                await mediaRepository.SaveSidecarsAsync(library.Id.Value, probed.ConvertAll(p => (p.Item.RelPath, p.Sidecar)), token);
        }
    }

    private async Task<SidecarData?> ReadSidecarAsync(ISidecarReader? reader, string rootPath, MediaItem item, CancellationToken ct)
    {
        if (reader is null || item.SidecarPath is null)
            return null;
        var result = await reader.ReadAsync(Path.Combine(rootPath, item.SidecarPath), ct);
        if (!result.IsSuccess)
            _logger.LogWarning("Ignoring unreadable sidecar {Path}: {Error}", item.SidecarPath, result.Error);
        return result.Value;
    }

    private async Task RunThumbnailsAsync(
        Library library,
        IMediaRepository mediaRepository,
        IThumbnailGenerator thumbnailGenerator,
        IThumbnailStore thumbnailStore,
        IngestOptions options,
        IProgressReporter progress,
        CancellationToken ct)
    {
        var libraryId = library.Id.Value;
        await mediaRepository.ResetOutdatedThumbnailsAsync(libraryId, ThumbnailFormat.Version, ct);
        var total = await mediaRepository.CountPendingThumbnailsAsync(libraryId, ct);
        if (total == 0)
            return;

        // Capacity = worker count: each job is picked as late as possible, so a scroll re-prioritises within one job.
        var queue = Channel.CreateBounded<MediaItem>(options.Workers);
        using var videoGate = new SemaphoreSlim(options.VideoWorkers);
        using var stages = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var throttle = new ProgressThrottle(TimeSpan.FromMilliseconds(100));
        var done = 0;

        var workers = Enumerable.Range(0, options.Workers).Select(_ => Task.Run(async () =>
        {
            await foreach (var item in queue.Reader.ReadAllAsync(stages.Token))
            {
                if (options.Pause is { } pause) await pause.WaitWhilePausedAsync(stages.Token);
                await GenerateThumbnailAsync(library, item, thumbnailGenerator, thumbnailStore, mediaRepository, progress, videoGate, options.Quality, stages.Token);
                var n = Interlocked.Increment(ref done);
                if (n >= total || throttle.Due())
                    progress.Report(new PipelineEvent("Generating thumbnails", Math.Min(n, total), total));
            }
        }, stages.Token));

        await Task.WhenAll(workers
            .Prepend(ProduceThumbnailJobsAsync(libraryId, mediaRepository, options.Viewport, queue.Writer, stages.Token))
            .Select(t => CancelOthersOnFault(t, stages)));
    }

    // Two lanes: what is on screen first, then everything else newest first.
    private static async Task ProduceThumbnailJobsAsync(
        int libraryId,
        IMediaRepository mediaRepository,
        ViewportHint viewport,
        ChannelWriter<MediaItem> writer,
        CancellationToken ct)
    {
        var queued = new HashSet<int>();
        var urgent = new Queue<MediaItem>();
        var normal = new Queue<MediaItem>();
        (DateTime DateTaken, int Id)? cursor = null;
        var normalExhausted = false;
        var seenViewport = -1;
        try
        {
            while (true)
            {
                if (viewport.Version != seenViewport)
                {
                    seenViewport = viewport.Version;
                    urgent = new Queue<MediaItem>(await mediaRepository.GetPendingThumbnailsByIdAsync(libraryId, viewport.Ids, ct));
                }

                if (urgent.Count == 0 && normal.Count == 0 && !normalExhausted)
                {
                    var page = await mediaRepository.GetPendingThumbnailsAsync(libraryId, ThumbnailPage, cursor, ct);
                    normalExhausted = page.Count < ThumbnailPage;
                    if (page.Count > 0)
                        cursor = (page[^1].DateTaken ?? DateTime.MinValue, page[^1].Id);
                    foreach (var item in page)
                        normal.Enqueue(item);
                }

                var next = urgent.Count > 0 ? urgent.Dequeue() : normal.Count > 0 ? normal.Dequeue() : null;
                if (next is null)
                    break;
                // An on-screen item also shows up in a later page while it is being generated.
                if (queued.Add(next.Id))
                    await writer.WriteAsync(next, ct);
            }
        }
        finally
        {
            writer.Complete();
        }
    }

    private async Task GenerateThumbnailAsync(
        Library library,
        MediaItem item,
        IThumbnailGenerator thumbnailGenerator,
        IThumbnailStore thumbnailStore,
        IMediaRepository mediaRepository,
        IProgressReporter progress,
        SemaphoreSlim videoGate,
        int quality,
        CancellationToken ct)
    {
        var job = new ThumbnailJob(item.Id, Path.Combine(library.RootPath, item.RelPath), item.Kind,
            ThumbnailFormat.Version, item.DurationMs, quality);

        var result = await GatedAsync(item.Kind, videoGate,
            () => thumbnailGenerator.GenerateSmallAsync(job, ct).AsTask(), ct);
        foreach (var delay in RetryDelays)
        {
            if (result.IsSuccess || ct.IsCancellationRequested)
                break;
            await Task.Delay(delay, ct);
            result = await GatedAsync(item.Kind, videoGate,
                () => thumbnailGenerator.GenerateSmallAsync(job, ct).AsTask(), ct);
        }

        // Cancelled mid-job: leave it pending so the next run picks it up.
        ct.ThrowIfCancellationRequested();

        if (result.IsSuccess)
        {
            // Drops the medium preview too: it shows the old file and is regenerated on next open.
            await thumbnailStore.DeleteAsync(item.Id, ct);
            await thumbnailStore.SaveAsync(item.Id, ThumbnailFormat.Small, result.Value!.Webp, ct);
            await mediaRepository.UpdateThumbAsync(item.Id, ThumbState.Done, ThumbnailFormat.Version, result.Value.AverageRgb, ct);
            progress.ThumbnailReady(item.Id, result.Value.AverageRgb);
        }
        else
        {
            _logger.LogWarning("Thumbnail failed for {Path}: {Error}", item.RelPath, result.Error);
            await mediaRepository.UpdateThumbAsync(item.Id, ThumbState.Failed, ThumbnailFormat.Version, null, ct);
        }
    }

    // Videos spawn ffprobe/ffmpeg processes; cap them separately from image work.
    private static async Task<T> GatedAsync<T>(MediaKind kind, SemaphoreSlim videoGate, Func<Task<T>> work, CancellationToken ct)
    {
        if (kind != MediaKind.Video)
            return await work();

        await videoGate.WaitAsync(ct);
        try
        {
            return await work();
        }
        finally
        {
            videoGate.Release();
        }
    }

    private static string NormalizePath(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsMediaExtension(string ext)
    {
        var e = ext.ToLowerInvariant();
        return e is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp" or ".avif"
            or ".mp4" or ".mkv" or ".webm" or ".mov" or ".avi" or ".m4v";
    }

    private static bool IsVideoExtension(string ext)
    {
        var e = ext.ToLowerInvariant();
        return e is ".mp4" or ".mkv" or ".webm" or ".mov" or ".avi" or ".m4v";
    }

    public void Dispose()
    {
        _toDiff.Writer.TryComplete();
        _toProbe.Writer.TryComplete();
    }

    /// <summary>Lets one caller through per interval, from any thread; keeps UI progress at ~10 updates/s.</summary>
    private sealed class ProgressThrottle(TimeSpan interval)
    {
        private long _last;

        public bool Due()
        {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _last);
            return now - last >= (long)interval.TotalMilliseconds
                   && Interlocked.CompareExchange(ref _last, now, last) == last;
        }
    }
}
