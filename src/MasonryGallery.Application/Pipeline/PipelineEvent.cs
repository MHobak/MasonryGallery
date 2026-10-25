namespace MasonryGallery.Application.Pipeline;

using System.Reactive.Linq;
using System.Reactive.Subjects;

public record PipelineEvent(string Message, int Completed, int Total);

/// <summary>Gallery changes collected over a short window, so the UI updates a few times a second at most.</summary>
/// <param name="ItemsChanged">Rows were added, removed or resized: the layout must be refetched.</param>
/// <param name="ReadyThumbnails">Thumbnails that became available, flattened as [id, rgb, id, rgb, ...].</param>
public sealed record GalleryChange(bool ItemsChanged, int[] ReadyThumbnails);

public interface IProgressReporter
{
    /// <summary>Status line updates.</summary>
    IObservable<PipelineEvent> Events { get; }

    IObservable<GalleryChange> Changes { get; }

    void Report(PipelineEvent evt);

    /// <summary>Signals that the set of items or their sizes changed.</summary>
    void ItemsChanged();

    /// <summary>Signals that one thumbnail is ready; the UI swaps it into that tile only.</summary>
    void ThumbnailReady(int mediaId, int averageRgb);
}

public sealed class PipelineProgressReporter : IProgressReporter
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(500);

    // Workers report from many threads; plain subjects require serialized calls.
    private readonly ISubject<PipelineEvent> _events = Subject.Synchronize(new Subject<PipelineEvent>());
    private readonly ISubject<(int Id, int Rgb)?> _changes = Subject.Synchronize(new Subject<(int Id, int Rgb)?>());

    public PipelineProgressReporter()
    {
        Changes = _changes
            .Buffer(Window)
            .Where(batch => batch.Count > 0)
            .Select(batch => new GalleryChange(
                batch.Any(c => c is null),
                batch.Where(c => c is not null).SelectMany(c => new[] { c!.Value.Id, c.Value.Rgb }).ToArray()))
            .Publish()
            .RefCount();
    }

    public IObservable<PipelineEvent> Events => _events;

    public IObservable<GalleryChange> Changes { get; }

    public void Report(PipelineEvent evt) => _events.OnNext(evt);

    public void ItemsChanged() => _changes.OnNext(null);

    public void ThumbnailReady(int mediaId, int averageRgb) => _changes.OnNext((mediaId, averageRgb));
}
