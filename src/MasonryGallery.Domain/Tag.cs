namespace MasonryGallery.Domain;

public sealed class Tag
{
    public int Id { get; internal set; }
    public string Name { get; init; } = string.Empty;

    /// <summary>Group from a hierarchical sidecar keyword ("Artist" for "Artist|name"); null for plain tags.</summary>
    public string? Category { get; init; }

    /// <summary>Read from the file's XMP sidecar; replaced when the sidecar changes, not editable in the app.</summary>
    public bool FromSidecar { get; init; }

    public Tag(string name)
    {
        Name = name;
    }

    public Tag()
    {
        Name = string.Empty;
    }

    public static Result<Tag> TryCreate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result<Tag>.Failure("Tag name cannot be empty");
        if (name.Length > 128)
            return Result<Tag>.Failure("Tag name exceeds 128 characters");
        return new Tag(name);
    }
}
