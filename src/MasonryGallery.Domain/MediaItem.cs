namespace MasonryGallery.Domain;

public sealed class MediaItem
{
    public int Id { get; internal set; }
    public LibraryId LibraryId { get; init; }
    public string RelPath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public MediaKind Kind { get; init; }
    public long SizeBytes { get; init; }
    public DateTime ModifiedUtc { get; init; }
    public DateTime? DateTaken { get; set; }
    public DateTime DateAdded { get; init; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long? DurationMs { get; set; }
    /// <summary>Videos: whether the WebView can likely play the codec. Null for images or before probing.</summary>
    public bool? Playable { get; set; }
    public byte[]? Placeholder { get; set; }
    public ProbeState ProbeState { get; set; }
    public ThumbState ThumbState { get; set; }
    public int ThumbVersion { get; set; }
    public int? ErrorCode { get; set; }
    public int? QuickHash { get; set; }
    public int ScanId { get; set; }
    public DateTime? DeletedAt { get; set; }
    public bool Favorite { get; set; }

    private List<Tag>? _tags;
    public IReadOnlyList<Tag> Tags => _tags ??= new List<Tag>();
    internal void SetTags(IReadOnlyList<Tag> tags) => _tags = new List<Tag>(tags);

    public MediaItem()
    {
        RelPath = string.Empty;
        FileName = string.Empty;
        DateAdded = DateTime.UtcNow;
    }

    public bool IsDeleted => DeletedAt.HasValue;
}
