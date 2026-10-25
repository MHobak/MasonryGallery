namespace MasonryGallery.Application.Pipeline;

/// <summary>
/// Media IDs the user is looking at that still lack a thumbnail. The UI writes it as the user scrolls;
/// the thumbnail stage serves these IDs before the rest of the queue.
/// </summary>
public sealed class ViewportHint
{
    private volatile int[] _ids = [];
    private int _version;

    public IReadOnlyList<int> Ids => _ids;

    /// <summary>Changes on every <see cref="Set"/>, so readers can tell when to re-query.</summary>
    public int Version => Volatile.Read(ref _version);

    public void Set(int[] ids)
    {
        _ids = ids;
        Interlocked.Increment(ref _version);
    }
}
