using FluentAssertions;
using MasonryGallery.Application.Pipeline;
using Xunit;

namespace MasonryGallery.Application.Tests;

public class ProgressReporterTests
{
    [Fact]
    public async Task Changes_are_batched_into_one_update_per_window()
    {
        var reporter = new PipelineProgressReporter();
        var received = new List<GalleryChange>();
        using var _ = reporter.Changes.Subscribe(received.Add);

        // Reported from several threads, as thumbnail workers do.
        await Task.WhenAll(
            Task.Run(() => reporter.ThumbnailReady(1, 0x112233)),
            Task.Run(() => reporter.ThumbnailReady(2, 0x445566)),
            Task.Run(reporter.ItemsChanged),
            Task.Run(reporter.ItemsChanged));
        await Task.Delay(800);

        // Normally one batch; two if a window boundary happens to fall between the reports.
        received.Should().HaveCountLessThanOrEqualTo(2);
        received.Should().Contain(c => c.ItemsChanged);
        received.SelectMany(c => c.ReadyThumbnails).Chunk(2).Select(p => (p[0], p[1]))
            .Should().BeEquivalentTo([(1, 0x112233), (2, 0x445566)]);
    }

    [Fact]
    public async Task Thumbnails_alone_do_not_request_a_relayout()
    {
        var reporter = new PipelineProgressReporter();
        var received = new List<GalleryChange>();
        using var _ = reporter.Changes.Subscribe(received.Add);

        reporter.ThumbnailReady(7, 0);
        await Task.Delay(800);

        received.Should().ContainSingle().Which.ItemsChanged.Should().BeFalse();
    }
}
