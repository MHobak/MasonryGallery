namespace MasonryGallery.Infrastructure.Thumbnails;

using System.Runtime.InteropServices;
using MasonryGallery.Application.Ports;
using Microsoft.Extensions.Logging;

public sealed class ShardedThumbnailStore : IThumbnailStore
{
    private readonly string _cacheRoot;
    private readonly string _version;
    private readonly ILogger<ShardedThumbnailStore> _logger;

    public ShardedThumbnailStore(string cacheRoot, string version, ILogger<ShardedThumbnailStore> logger)
    {
        _cacheRoot = cacheRoot;
        _version = version;
        _logger = logger;
        Directory.CreateDirectory(Path.Combine(cacheRoot, version));
    }

    private string GetSizeDir(int size) => size == 320 ? "small" : "medium";

    /// <summary>Where the thumbnail lives (or would live). Lets the media server stream it from disk.</summary>
    public string GetPath(int mediaId, int size)
        => Path.Combine(_cacheRoot, _version, GetSizeDir(size), (mediaId % 256).ToString("x2"), $"{mediaId}.webp");

    public async Task<ReadOnlyMemory<byte>?> GetAsync(int mediaId, int size, CancellationToken ct = default)
    {
        var path = GetPath(mediaId, size);

        try
        {
            if (!File.Exists(path))
                return null;

            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var data = new byte[fs.Length];
            await fs.ReadExactlyAsync(data, ct);
            return new ReadOnlyMemory<byte>(data);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task SaveAsync(int mediaId, int size, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var dir = GetSizeDir(size);
        var shard = (mediaId % 256).ToString("x2");
        var shardPath = Path.Combine(_cacheRoot, _version, dir, shard);
        Directory.CreateDirectory(shardPath);

        var tempPath = Path.Combine(shardPath, $"{mediaId}.tmp.{Path.GetRandomFileName()}");
        var finalPath = Path.Combine(shardPath, $"{mediaId}.webp");

        try
        {
            await using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.Asynchronous);
            await fs.WriteAsync(data, ct);
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public Task DeleteAsync(int mediaId, CancellationToken ct = default)
    {
        foreach (var size in new[] { 320, 960 })
        {
            var dir = GetSizeDir(size);
            var shard = (mediaId % 256).ToString("x2");
            var path = Path.Combine(_cacheRoot, _version, dir, shard, $"{mediaId}.webp");
            try { File.Delete(path); } catch { }
        }
        return Task.CompletedTask;
    }

    public Task<int> CleanOrphansAsync(IReadOnlySet<int> knownIds, DateTime olderThanUtc, CancellationToken ct = default)
    {
        var versionDir = Path.Combine(_cacheRoot, _version);
        if (!Directory.Exists(versionDir))
            return Task.FromResult(0);

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(versionDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            // "{id}.webp" is a thumbnail; anything else (e.g. "{id}.tmp.xyz" from a crash mid-save) is debris.
            var name = Path.GetFileName(file);
            var isThumbnail = name.EndsWith(".webp", StringComparison.Ordinal)
                              && int.TryParse(name.AsSpan(0, name.Length - ".webp".Length), out var id)
                              && knownIds.Contains(id);
            if (isThumbnail)
                continue;

            try
            {
                // Newer files may belong to media added after the ID snapshot; leave them for the next run.
                if (File.GetLastWriteTimeUtc(file) >= olderThanUtc)
                    continue;
                File.Delete(file);
                removed++;
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not delete {File}", file);
            }
        }
        return Task.FromResult(removed);
    }

    public Task DeleteStaleVersionsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_cacheRoot))
            return Task.CompletedTask;

        foreach (var dir in Directory.EnumerateDirectories(_cacheRoot, "v*"))
        {
            var name = Path.GetFileName(dir);
            if (name == _version || !int.TryParse(name.AsSpan(1), out _))
                continue;
            try
            {
                Directory.Delete(dir, recursive: true);
                _logger.LogInformation("Deleted thumbnail cache {Dir} from an older version", dir);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not delete old thumbnail cache {Dir}", dir);
            }
        }
        return Task.CompletedTask;
    }
}
