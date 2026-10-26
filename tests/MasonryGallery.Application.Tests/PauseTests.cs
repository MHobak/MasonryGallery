using FluentAssertions;
using MasonryGallery.Application.Pipeline;
using Xunit;

namespace MasonryGallery.Application.Tests;

public class IndexingPauseTests
{
    private readonly IndexingPause _pause = new();

    [Fact]
    public async Task Running_does_not_wait()
    {
        _pause.IsPaused.Should().BeFalse();
        await _pause.WaitWhilePausedAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Paused_waits_until_resumed_and_reports_each_change_once()
    {
        var changes = 0;
        _pause.Changed += () => changes++;

        _pause.Pause();
        _pause.Pause();
        var wait = _pause.WaitWhilePausedAsync(CancellationToken.None);
        await Task.Delay(50);
        wait.IsCompleted.Should().BeFalse();

        _pause.Resume();
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
        _pause.Resume();
        changes.Should().Be(2);
    }

    [Fact]
    public async Task Waiting_honours_cancellation()
    {
        _pause.Pause();
        using var cts = new CancellationTokenSource(50);

        await _pause.Invoking(p => p.WaitWhilePausedAsync(cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        _pause.IsPaused.Should().BeTrue("cancelling one wait doesn't resume indexing");
    }
}
