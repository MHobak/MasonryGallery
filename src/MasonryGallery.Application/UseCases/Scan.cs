namespace MasonryGallery.Application.UseCases;

using System.Collections.ObjectModel;
using MasonryGallery.Application.Pipeline;
using MasonryGallery.Application.Ports;
using MasonryGallery.Domain;
using Microsoft.Extensions.Logging;

/// <summary>What the scan scheduler drives; implemented by <see cref="Scan"/>.</summary>
public interface ILibraryScanner
{
    /// <summary>Walks the whole library: indexes, sweeps deletions, updates status.</summary>
    Task<Result> ExecuteAsync(LibraryId libraryId, CancellationToken ct = default);

    /// <summary>Re-indexes only the given paths (files or folders, relative to the root).</summary>
    /// <returns>Paths skipped because their file is still being written; retry them shortly.</returns>
    Task<Result<IReadOnlyCollection<string>>> ExecutePathsAsync(
        LibraryId libraryId, IReadOnlyCollection<string> relPaths, CancellationToken ct = default);
}

public sealed class Scan : ILibraryScanner
{
    // A file modified this recently is probably still being copied; indexing it now would probe a partial file.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    private readonly ILibraryRepository _libraryRepository;
    private readonly IMediaRepository _mediaRepository;
    private readonly IFileEnumerator _fileEnumerator;
    private readonly IMediaProbe _mediaProbe;
    private readonly IThumbnailGenerator _thumbnailGenerator;
    private readonly IThumbnailStore _thumbnailStore;
    private readonly IClock _clock;
    private readonly IProgressReporter _progress;
    private readonly ILogger<Scan> _logger;
    private readonly ViewportHint _viewport;

    public Scan(
        ILibraryRepository libraryRepository,
        IMediaRepository mediaRepository,
        IFileEnumerator fileEnumerator,
        IMediaProbe mediaProbe,
        IThumbnailGenerator thumbnailGenerator,
        IThumbnailStore thumbnailStore,
        IClock clock,
        IProgressReporter progress,
        ILogger<Scan> logger,
        ViewportHint? viewport = null)
    {
        _viewport = viewport ?? new ViewportHint();
        _libraryRepository = libraryRepository;
        _mediaRepository = mediaRepository;
        _fileEnumerator = fileEnumerator;
        _mediaProbe = mediaProbe;
        _thumbnailGenerator = thumbnailGenerator;
        _thumbnailStore = thumbnailStore;
        _clock = clock;
        _progress = progress;
        _logger = logger;
    }

    public async Task<Result> ExecuteAsync(LibraryId libraryId, CancellationToken ct = default)
    {
        var libraryResult = await _libraryRepository.GetByIdAsync(libraryId, ct);
        if (!libraryResult.IsSuccess)
            return Result.Failure(libraryResult.Error!);

        var library = libraryResult.Value!;
        // Any stored status is accepted: availability is decided by the disk, not by the last run.
        // A crash mid-scan leaves "Scanning" in the DB, and an offline drive may have been plugged back in.
        if (!_fileEnumerator.RootExists(library.RootPath))
            return await MarkOfflineAsync(libraryId, "Library folder is not available", ct);

        await _libraryRepository.SetStatusAsync(libraryId, LibraryStatus.Scanning, ct);

        var scanId = await _libraryRepository.GetNextScanIdAsync(libraryId, ct);

        try
        {
            var pipeline = new IngestPipeline(_logger);
            var filesSeen = await pipeline.RunAsync(
                library,
                _fileEnumerator.EnumerateAsync(library.RootPath, library.FollowSymlinks, ct),
                _mediaRepository,
                _mediaProbe,
                _thumbnailGenerator,
                _thumbnailStore,
                scanId,
                OptionsFor(library.RootPath),
                _progress,
                ct);

            // An unmounted NAS share or a vanished drive often leaves an empty directory behind.
            // Sweeping then would delete the whole library, so treat it as offline instead.
            if (filesSeen == 0 && await _mediaRepository.CountLiveAsync(libraryId.Value, ct) > 0)
                return await MarkOfflineAsync(libraryId, "Library folder is empty; keeping existing items", ct);

            var purged = await _mediaRepository.SoftDeleteOldScansAsync(libraryId.Value, scanId, 30, ct);
            if (purged > 0)
                await CleanThumbnailCacheAsync(ct);
            _progress.ItemsChanged(); // the sweep may have removed rows
            await _libraryRepository.UpdateAsync(new Library(library.Name, library.RootPath)
            {
                Id = libraryId, Enabled = library.Enabled, Status = LibraryStatus.Online,
                FollowSymlinks = library.FollowSymlinks, LastScanId = scanId, LastScanUtc = _clock.UtcNow
            }, ct);
            _progress.Report(new PipelineEvent("Up to date", 0, 0));
            return Result.Success();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // App shutdown: not a failure. Nothing was swept, so the library is as valid as before.
            await _libraryRepository.SetStatusAsync(libraryId, LibraryStatus.Online, CancellationToken.None);
            return Result.Failure("Cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan failed for library {LibraryId}", libraryId.Value);
            await _libraryRepository.SetStatusAsync(libraryId, LibraryStatus.Error, ct);
            return Result.Failure(ex.Message);
        }
    }

    public async Task<Result<IReadOnlyCollection<string>>> ExecutePathsAsync(
        LibraryId libraryId, IReadOnlyCollection<string> relPaths, CancellationToken ct = default)
    {
        var libraryResult = await _libraryRepository.GetByIdAsync(libraryId, ct);
        if (!libraryResult.IsSuccess)
            return Result<IReadOnlyCollection<string>>.Failure(libraryResult.Error!);
        var library = libraryResult.Value!;
        if (!_fileEnumerator.RootExists(library.RootPath))
            return Result<IReadOnlyCollection<string>>.Failure((await MarkOfflineAsync(libraryId, "Library folder is not available", ct)).Error!);

        var files = new List<FileEntry>();
        var folders = new List<string>();
        var gone = new List<string>();
        var deferred = new List<string>();
        foreach (var path in Collapse(relPaths))
        {
            // Creating or deleting .nomedia changes what its whole folder should show.
            if (Path.GetFileName(path) == ".nomedia")
            {
                folders.Add(Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "");
                continue;
            }

            var info = _fileEnumerator.Inspect(library.RootPath, path, library.FollowSymlinks);
            switch (info.Kind)
            {
                case PathKind.Missing:
                    gone.Add(path);
                    break;
                case PathKind.Directory:
                    folders.Add(path);
                    break;
                case PathKind.File when _clock.UtcNow - info.File!.ModifiedUtc < SettleTime:
                    deferred.Add(path);
                    break;
                case PathKind.File:
                    files.Add(info.File!);
                    break;
            }
        }

        // The root itself (e.g. .nomedia at the top) is a full rescan's job.
        if (folders.Contains(""))
        {
            var full = await ExecuteAsync(libraryId, ct);
            return full.IsSuccess ? deferred : Result<IReadOnlyCollection<string>>.Failure(full.Error!);
        }

        // A fresh ID per run: rows under a rescanned folder that this run didn't stamp are gone.
        var scanId = await _libraryRepository.GetNextScanIdAsync(libraryId, ct);
        await _libraryRepository.SetLastScanIdAsync(libraryId, scanId, ct);
        if (files.Count > 0 || folders.Count > 0)
        {
            await new IngestPipeline(_logger).RunAsync(
                library, Concat(files, folders, library, ct), _mediaRepository, _mediaProbe, _thumbnailGenerator,
                _thumbnailStore, scanId, OptionsFor(library.RootPath), _progress, ct);
        }

        var removed = gone.Count > 0 ? await _mediaRepository.SoftDeletePathsAsync(libraryId.Value, gone, ct) : 0;
        foreach (var folder in folders)
            removed += await _mediaRepository.SoftDeleteUnseenUnderAsync(libraryId.Value, folder, scanId, ct);
        if (removed > 0)
            _progress.ItemsChanged();

        _logger.LogInformation("Updated {Files} files and {Folders} folders, removed {Removed} items in library {LibraryId}",
            files.Count, folders.Count, removed, libraryId.Value);
        _progress.Report(new PipelineEvent("Up to date", 0, 0));
        return deferred;
    }

    // Drops paths inside a folder that is itself in the set; the folder rescan covers them.
    private static IEnumerable<string> Collapse(IReadOnlyCollection<string> relPaths)
    {
        var all = relPaths.Select(p => p.Replace('\\', '/').Trim('/')).ToHashSet(StringComparer.Ordinal);
        return all.Where(p => !AncestorsOf(p).Any(all.Contains)).Order(StringComparer.Ordinal);
    }

    private static IEnumerable<string> AncestorsOf(string relPath)
    {
        for (var i = relPath.LastIndexOf('/'); i > 0; i = relPath.LastIndexOf('/', i - 1))
            yield return relPath[..i];
    }

    private async IAsyncEnumerable<FileEntry> Concat(
        List<FileEntry> files, List<string> folders, Library library,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var file in files)
            yield return file;
        foreach (var folder in folders)
        {
            await foreach (var entry in _fileEnumerator.EnumerateSubtreeAsync(library.RootPath, folder, library.FollowSymlinks, ct))
                yield return entry;
        }
    }

    // Seeks dominate on spinning disks and latency on network shares; more workers only add thrashing there.
    private IngestOptions OptionsFor(string rootPath)
    {
        var slow = _fileEnumerator.IsNetworkPath(rootPath) || _fileEnumerator.IsRotationalDisk(rootPath);
        var workers = slow ? 2 : Math.Max(1, Environment.ProcessorCount - 1);
        var videoWorkers = Math.Clamp(Environment.ProcessorCount / 2, 1, workers);
        return new IngestOptions(workers, videoWorkers, _viewport);
    }

    // Only after rows were permanently deleted can thumbnails become orphaned, so the walk is rare.
    private async Task CleanThumbnailCacheAsync(CancellationToken ct)
    {
        var snapshotTaken = _clock.UtcNow;
        var ids = await _mediaRepository.GetAllIdsAsync(ct);
        var removed = await _thumbnailStore.CleanOrphansAsync(ids, snapshotTaken, ct);
        _logger.LogInformation("Removed {Count} orphaned thumbnails", removed);
    }

    private async Task<Result> MarkOfflineAsync(LibraryId libraryId, string reason, CancellationToken ct)
    {
        _logger.LogWarning("Library {LibraryId} is offline: {Reason}", libraryId.Value, reason);
        await _libraryRepository.SetStatusAsync(libraryId, LibraryStatus.Offline, ct);
        return Result.Failure(reason);
    }
}
