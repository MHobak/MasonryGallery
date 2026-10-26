namespace MasonryGallery.Application.Pipeline;

/// <summary>Thumbnail settings shared by the pipeline, the store and the media server (design §6).</summary>
public static class ThumbnailFormat
{
    /// <summary>
    /// Bump when generator output changes. Rows with an older ThumbVersion are regenerated,
    /// and the cache folder for the old version is deleted.
    /// v2: EXIF orientation applied, video frames fit a 320 px long edge.
    /// </summary>
    public const int Version = 2;

    /// <summary>Long edge of grid thumbnails.</summary>
    public const int Small = 320;

    /// <summary>Long edge of lightbox previews, generated on first open.</summary>
    public const int Medium = 960;

    /// <summary>WebP quality used unless the user picks another.</summary>
    public const int DefaultQuality = 80;
}
