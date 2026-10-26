namespace MasonryGallery.UI;

/// <param name="IsViewer">
/// A second window on an index another process already runs. It browses, presents and edits tags and favorites,
/// but scanning, library management and settings stay with the main window, so two processes never index the
/// same database.
/// </param>
public sealed record AppMode(bool IsViewer);
