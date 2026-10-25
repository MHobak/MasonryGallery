namespace MasonryGallery.Infrastructure.Video;

using System.Diagnostics;
using MasonryGallery.Application.Ports;
using MasonryGallery.Domain;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

public sealed class FfprobeProbe : IMediaProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _ffprobePath;
    private readonly ILogger<FfprobeProbe> _logger;

    public FfprobeProbe(string ffprobePath, ILogger<FfprobeProbe> logger)
    {
        _ffprobePath = ffprobePath;
        _logger = logger;
    }

    public async ValueTask<Result<MediaMetadata>> ProbeAsync(string filePath, MediaKind kind, CancellationToken ct = default)
    {
        if (kind != MediaKind.Video)
            return Result<MediaMetadata>.Failure("Ffprobe only handles videos");

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = _ffprobePath,
                ArgumentList =
                {
                    "-v", "quiet",
                    "-print_format", "json",
                    "-show_streams",
                    "-show_format",
                    filePath
                },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process == null)
                return Result<MediaMetadata>.Failure("Cannot start ffprobe");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            // Read while it runs: output past the 64 KB pipe buffer (many streams, chapters) would otherwise
            // block ffprobe forever waiting for a reader.
            string output;
            try
            {
                output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return Result<MediaMetadata>.Failure("ffprobe timed out");
            }

            if (process.ExitCode != 0)
                return Result<MediaMetadata>.Failure($"ffprobe exited with code {process.ExitCode}");

            var result = JsonConvert.DeserializeObject<ProbeResult>(output);

            if (result?.Streams == null || !result.Streams.Any())
                return Result<MediaMetadata>.Failure("No streams found");

            var videoStream = result.Streams.FirstOrDefault(s => s.CodecType == "video");
            if (videoStream == null)
                return Result<MediaMetadata>.Failure("No video stream found");

            var durationMs = result.Format?.DurationMs;
            var width = videoStream.Width;
            var height = videoStream.Height;
            var rotation = Rotation(videoStream);
            // Phone videos are stored landscape with a rotation flag; the player shows them turned.
            if (rotation is 90 or 270)
                (width, height) = (height, width);

            var playableCodecs = new[] { "h264", "hevc", "vp8", "vp9", "av1" };
            var codecName = videoStream.CodecName ?? "";
            var isPlayable = playableCodecs.Contains(codecName, StringComparer.OrdinalIgnoreCase);

            return new MediaMetadata(width, height, durationMs, null, rotation, isPlayable);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe video {Path}", filePath);
            return Result<MediaMetadata>.Failure(ex.Message);
        }
    }

    // ffmpeg 5+ reports rotation in the display-matrix side data (e.g. -90); older builds used a "rotate" tag (e.g. 90).
    // Normalized to 0/90/180/270.
    private static int Rotation(ProbeStream stream)
    {
        var degrees = stream.SideData?.FirstOrDefault(d => d.Rotation is not null)?.Rotation
                      ?? (int.TryParse(stream.Tags?.GetValueOrDefault("rotate"), out var tag) ? tag : 0);
        return ((int)Math.Round(degrees) % 360 + 360) % 360;
    }

    private sealed class ProbeResult
    {
        [JsonProperty("streams")]
        public IReadOnlyList<ProbeStream>? Streams { get; init; }

        [JsonProperty("format")]
        public ProbeFormat? Format { get; init; }
    }

    private sealed class ProbeStream
    {
        [JsonProperty("width")]
        public int Width { get; init; }

        [JsonProperty("height")]
        public int Height { get; init; }

        [JsonProperty("codec_name")]
        public string? CodecName { get; init; }

        [JsonProperty("codec_type")]
        public string? CodecType { get; init; }

        [JsonProperty("tags")]
        public Dictionary<string, string>? Tags { get; init; }

        [JsonProperty("side_data_list")]
        public IReadOnlyList<ProbeSideData>? SideData { get; init; }
    }

    private sealed class ProbeSideData
    {
        [JsonProperty("rotation")]
        public double? Rotation { get; init; }
    }

    private sealed class ProbeFormat
    {
        [JsonProperty("duration")]
        public double Duration { get; init; }

        public long DurationMs => (long)(Duration * 1000);
    }
}
