namespace MasonryGallery.Domain;

public sealed class Tag
{
    public int Id { get; internal set; }
    public string Name { get; init; } = string.Empty;

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
