namespace MasonryGallery.Infrastructure.Video;

using System.Diagnostics;
using System.Globalization;
using MasonryGallery.Application.Ports;
using MasonryGallery.Domain;
using MasonryGallery.Infrastructure.Imaging;
using Microsoft.Extensions.Logging;
using SkiaSharp;

public sealed class FfmpegThumbnailGenerator
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _ffmpegPath;
    private readonly ILogger<FfmpegThumbnailGenerator> _logger;

    public FfmpegThumbnailGenerator(string ffmpegPath, ILogger<FfmpegThumbnailGenerator> logger)
    {
        _ffmpegPath = ffmpegPath;
        _logger = logger;
    }

    public async ValueTask<Result<ThumbnailImage>> GenerateThumbnailAsync(ThumbnailJob job, int size, CancellationToken ct = default)
    {
        if (job.Kind != MediaKind.Video)
            return Result<ThumbnailImage>.Failure("FfmpegThumbnailGenerator only handles videos");

        try
        {
            // 10% in skips black intro frames; files that can't seek there get a second try from the start.
            var seekSeconds = job.DurationMs is > 0 ? job.DurationMs.Value / 10_000.0 : 0;
            var frame = await ExtractFrameAsync(job, size, seekSeconds, ct);
            if (!frame.IsSuccess && seekSeconds > 0)
                frame = await ExtractFrameAsync(job, size, 0, ct);
            if (!frame.IsSuccess)
                return Result<ThumbnailImage>.Failure(frame.Error!);

            // ffmpeg hands over a PNG; re-encode so the cache holds WebP like image thumbnails do.
            // ffmpeg already applies the stream's rotation, so the frame is upright.
            using var bitmap = SKBitmap.Decode(frame.Value!);
            if (bitmap == null)
                return Result<ThumbnailImage>.Failure("Cannot decode ffmpeg frame");
            return ThumbnailEncoder.Encode(bitmap, SKEncodedOrigin.TopLeft, size);
        }
        catch (OperationCanceledException)
        {
            return Result<ThumbnailImage>.Failure("Cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Thumbnail generation failed for video {MediaId}", job.MediaId);
            return Result<ThumbnailImage>.Failure(ex.Message);
        }
    }

    private async Task<Result<byte[]>> ExtractFrameAsync(ThumbnailJob job, int size, double seekSeconds, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                ArgumentList =
                {
                    "-loglevel", "error",
                    "-ss", seekSeconds.ToString("0.###", CultureInfo.InvariantCulture), // before -i: fast keyframe seek
                    "-i", job.FilePath,
                    "-frames:v", "1",
                    "-vf", $"scale=w={size}:h={size}:force_original_aspect_ratio=decrease", // long edge = size
                    "-f", "image2pipe",
                    "-c:v", "png",
                    "pipe:1"
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        if (!process.Start())
            return Result<byte[]>.Failure("Cannot start ffmpeg");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        // Drain both pipes concurrently; a full stderr buffer would otherwise stall ffmpeg.
        using var frame = new MemoryStream();
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(frame, timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await stdout;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            _logger.LogWarning("FFmpeg timed out for {MediaId}", job.MediaId);
            return Result<byte[]>.Failure("FFmpeg timed out");
        }

        if (process.ExitCode != 0 || frame.Length == 0)
        {
            _logger.LogWarning("FFmpeg failed for {MediaId} at {Seek}s: {Error}", job.MediaId, seekSeconds, await stderr);
            return Result<byte[]>.Failure("FFmpeg generation failed");
        }

        return frame.ToArray();
    }
}
