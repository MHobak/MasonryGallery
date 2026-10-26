namespace MasonryGallery.UI;

/// <summary>Native window operations the host supplies (Photino). Unset in tests and other hosts.</summary>
public sealed class WindowControl
{
    /// <summary>Puts the app window in or out of full screen.</summary>
    public Action<bool>? SetFullScreen { get; set; }

    /// <summary>Opens another window on the same index, as a viewer (see <see cref="AppMode"/>).</summary>
    public Action? OpenNewWindow { get; set; }
}
