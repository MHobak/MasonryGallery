namespace MasonryGallery.Application.Ports;

using MasonryGallery.Domain;

/// <param name="Name">Keyword as written in the sidecar.</param>
/// <param name="Category">Parent of a hierarchical keyword ("Artist" for "Artist|name"), or null.</param>
public record SidecarTag(string Name, string? Category);

/// <summary>What an XMP sidecar says about its media file. Missing values are null.</summary>
public record SidecarData(
    IReadOnlyList<SidecarTag> Tags,
    string? Creator = null,
    string? Source = null,
    string? Title = null,
    string? Description = null,
    int? Rating = null,
    DateTime? DateTaken = null);

public interface ISidecarReader
{
    ValueTask<Result<SidecarData>> ReadAsync(string sidecarPath, CancellationToken ct = default);
}
