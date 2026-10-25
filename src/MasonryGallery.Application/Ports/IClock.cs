namespace MasonryGallery.Application.Ports;

public interface IClock
{
    DateTime UtcNow { get; }
    Task Delay(TimeSpan delay, CancellationToken ct = default);
}
