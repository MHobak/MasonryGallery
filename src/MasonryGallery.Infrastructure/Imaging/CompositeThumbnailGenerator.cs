namespace MasonryGallery.Infrastructure.Imaging;

using MasonryGallery.Application.Pipeline;
using MasonryGallery.Application.Ports;
using MasonryGallery.Domain;
using MasonryGallery.Infrastructure.Video;

/// <summary>
/// Delegates thumbnail generation to SkiaSharp for images and FFmpeg for videos.
/// </summary>
public sealed class CompositeThumbnailGenerator : IThumbnailGenerator
{
    private readonly IThumbnailGenerator _imageGenerator;
    private readonly FfmpegThumbnailGenerator _videoGenerator;

    public CompositeThumbnailGenerator(IThumbnailGenerator imageGenerator, FfmpegThumbnailGenerator videoGenerator)
    {
        _imageGenerator = imageGenerator;
        _videoGenerator = videoGenerator;
    }

    public ValueTask<Result<ThumbnailImage>> GenerateSmallAsync(ThumbnailJob job, CancellationToken ct = default)
        => job.Kind == MediaKind.Image
            ? _imageGenerator.GenerateSmallAsync(job, ct)
            : _videoGenerator.GenerateThumbnailAsync(job, ThumbnailFormat.Small, ct);

    public ValueTask<Result<ThumbnailImage>> GenerateMediumAsync(ThumbnailJob job, CancellationToken ct = default)
        => job.Kind == MediaKind.Image
            ? _imageGenerator.GenerateMediumAsync(job, ct)
            : _videoGenerator.GenerateThumbnailAsync(job, ThumbnailFormat.Medium, ct);
}
