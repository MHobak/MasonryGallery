using FluentAssertions;
using MasonryGallery.Application.Pipeline;
using MasonryGallery.Application.Ports;
using MasonryGallery.Application.UseCases;
using MasonryGallery.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace MasonryGallery.Application.Tests;

public class UseCaseTests
{
    private static readonly LibraryId Id = new(1);

    private static (Scan scan, ILibraryRepository libs, IFileEnumerator files, IMediaRepository media) Build(Library lib)
    {
        var libs = Substitute.For<ILibraryRepository>();
        libs.GetByIdAsync(Id, Arg.Any<CancellationToken>()).Returns(Result<Library>.Success(lib));
        libs.GetNextScanIdAsync(Id, Arg.Any<CancellationToken>()).Returns(7);
        var files = Substitute.For<IFileEnumerator>();
        files.RootExists(Arg.Any<string>()).Returns(true);
        files.EnumerateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<FileEntry>());
        var media = Substitute.For<IMediaRepository>();
        media.GetScanStatesAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ScanState>());
        var scan = new Scan(libs, media, files,
            Substitute.For<IMediaProbe>(), Substitute.For<IThumbnailGenerator>(),
            Substitute.For<IThumbnailStore>(), Substitute.For<IClock>(),
            Substitute.For<IProgressReporter>(), NullLogger<Scan>.Instance);
        return (scan, libs, files, media);
    }

    [Fact]
    public async Task AddLibrary_delegates_to_repository()
    {
        var libs = Substitute.For<ILibraryRepository>();
        libs.AddAsync(Arg.Any<Library>(), Arg.Any<CancellationToken>())
            .Returns(c => Result<Library>.Success(c.Arg<Library>()));
        var r = await new AddLibrary(libs).ExecuteAsync("Pics", "/pics");
        r.Value!.Name.Should().Be("Pics");
        r.Value.RootPath.Should().Be("/pics");
    }

    [Fact]
    public async Task Scan_fails_when_library_missing()
    {
        var (scan, libs, _, _) = Build(new Library());
        libs.GetByIdAsync(Id, Arg.Any<CancellationToken>()).Returns(Result<Library>.Failure("nope"));
        (await scan.ExecuteAsync(Id)).Error.Should().Be("nope");
    }

    [Fact]
    public async Task Scan_marks_library_offline_and_keeps_items_when_root_missing()
    {
        var (scan, libs, files, media) = Build(new Library("n", "/p"));
        files.RootExists("/p").Returns(false);
        (await scan.ExecuteAsync(Id)).IsSuccess.Should().BeFalse();
        await libs.Received().SetStatusAsync(Id, LibraryStatus.Offline, Arg.Any<CancellationToken>());
        await libs.DidNotReceive().SetStatusAsync(Id, LibraryStatus.Scanning, Arg.Any<CancellationToken>());
        await media.DidNotReceiveWithAnyArgs().SoftDeleteOldScansAsync(default, default, default, default);
    }

    [Fact]
    public async Task Scan_keeps_items_when_root_is_empty_but_library_had_media()
    {
        // An unmounted NAS share: the mount point exists but is empty.
        var (scan, libs, _, media) = Build(new Library("n", "/p"));
        media.CountLiveAsync(1, Arg.Any<CancellationToken>()).Returns(42);
        (await scan.ExecuteAsync(Id)).IsSuccess.Should().BeFalse();
        await libs.Received().SetStatusAsync(Id, LibraryStatus.Offline, Arg.Any<CancellationToken>());
        await media.DidNotReceiveWithAnyArgs().SoftDeleteOldScansAsync(default, default, default, default);
    }

    [Fact]
    public async Task Scan_brings_offline_library_back_when_root_returns()
    {
        var (scan, libs, _, _) = Build(new Library("n", "/p") { Status = LibraryStatus.Offline });
        (await scan.ExecuteAsync(Id)).IsSuccess.Should().BeTrue();
        await libs.Received().UpdateAsync(Arg.Is<Library>(l => l.Status == LibraryStatus.Online), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Scan_recovers_library_stuck_in_scanning()
    {
        var (scan, _, _, _) = Build(new Library("n", "/p") { Status = LibraryStatus.Scanning });
        (await scan.ExecuteAsync(Id)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Scan_marks_library_error_when_pipeline_throws()
    {
        var (scan, libs, files, _) = Build(new Library("n", "/p"));
        files.EnumerateAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IAsyncEnumerable<FileEntry>>(_ => throw new IOException("disk"));
        var r = await scan.ExecuteAsync(Id);
        r.IsSuccess.Should().BeFalse();
        await libs.Received().SetStatusAsync(Id, LibraryStatus.Error, Arg.Any<CancellationToken>());
    }
}
