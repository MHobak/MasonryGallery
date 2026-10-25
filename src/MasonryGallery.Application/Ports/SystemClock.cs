namespace MasonryGallery.Application.Ports;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;

    public Task Delay(TimeSpan delay, CancellationToken ct = default)
        => Task.Delay(delay, ct);
}
