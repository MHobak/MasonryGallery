namespace MasonryGallery.UI;

/// <summary>Set by the host to open the native folder dialog; null when unavailable (typed path only).</summary>
public sealed class FolderPicker
{
    public Func<string?>? Pick { get; set; }
}
