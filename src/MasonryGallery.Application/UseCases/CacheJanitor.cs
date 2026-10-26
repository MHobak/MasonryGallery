namespace MasonryGallery.Application.UseCases;

using MasonryGallery.Application.Pipeline;
using MasonryGallery.Application.Ports;
using MasonryGallery.Application.Settings;
using Microsoft.Extensions.Logging;

/// <summary>
/// Keeps lightbox previews within the user's size limit (design §6). Previews are recreated on demand,
/// so evicting one costs a short wait the next time it's opened. Grid thumbnails are left alone.
/// Orphaned thumbnails are removed by full scans and library removal, not here.
/// </summary>
public sealed class CacheJanitor : IDisposable
{
    private readonly IThumbnailStore _store;
    private readonly ISettingsStore _settings;
    private readonly ILogger<CacheJanitor> _logger;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    public CacheJanitor(IThumbnailStore store, ISettingsStore settings, ILogger<CacheJanitor> logger, TimeSpan? interval = null)
    {
        _store = store;
        _settings = settings;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromHours(1);
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Trims now and then every interval, until disposed.</summary>
    public void Start() => _ = RunAsync(_stop.Token);

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                await TrimAsync(ct);
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    private void OnSettingsChanged(AppSettings before, AppSettings after)
    {
        if (after.PreviewCacheLimitMb != before.PreviewCacheLimitMb)
            _ = TrimAsync(_stop.Token);
    }

    /// <summary>Applies the current limit; returns what was deleted. Failures are logged, never thrown.</summary>
    public async Task<CacheUsage> TrimAsync(CancellationToken ct = default)
    {
        var limitMb = _settings.Current.PreviewCacheLimitMb;
        if (limitMb == 0)
            return new CacheUsage(0, 0);
        return await RunExclusiveAsync(() => _store.TrimAsync(ThumbnailFormat.Medium, limitMb * 1024L * 1024L, ct), ct);
    }

    /// <summary>Deletes every preview; they come back as items are opened.</summary>
    public Task<CacheUsage> ClearPreviewsAsync(CancellationToken ct = default)
        => RunExclusiveAsync(() => _store.TrimAsync(ThumbnailFormat.Medium, 0, ct), ct);

    public async Task<(CacheUsage Thumbnails, CacheUsage Previews)> GetUsageAsync(CancellationToken ct = default)
    {
        // A directory walk; keep it off the caller's (UI) thread.
        return await Task.Run(async () =>
            (await _store.GetUsageAsync(ThumbnailFormat.Small, ct), await _store.GetUsageAsync(ThumbnailFormat.Medium, ct)), ct);
    }

    private async Task<CacheUsage> RunExclusiveAsync(Func<Task<CacheUsage>> trim, CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return new CacheUsage(0, 0);
        }
        try
        {
            return await Task.Run(trim, ct);
        }
        catch (OperationCanceledException)
        {
            return new CacheUsage(0, 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Preview cache trim failed");
            return new CacheUsage(0, 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _stop.Cancel();
    }
}
