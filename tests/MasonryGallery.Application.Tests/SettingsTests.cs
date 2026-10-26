using FluentAssertions;
using MasonryGallery.Application.Pipeline;
using MasonryGallery.Application.Ports;
using MasonryGallery.Application.Settings;
using MasonryGallery.Application.UseCases;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace MasonryGallery.Application.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Defaults_are_valid()
        => new AppSettings().Validate().IsSuccess.Should().BeTrue();

    [Theory]
    [InlineData(0, null, 80, 0, "Workers")]
    [InlineData(null, 65, 80, 0, "Video workers")]
    [InlineData(null, null, 40, 0, "quality")]
    [InlineData(null, null, 99, 0, "quality")]
    [InlineData(null, null, 80, -1, "negative")]
    public void Out_of_range_values_are_rejected(int? workers, int? video, int quality, int cacheMb, string mentions)
    {
        var result = new AppSettings
        {
            Workers = workers,
            VideoWorkers = video,
            ThumbnailQuality = quality,
            PreviewCacheLimitMb = cacheMb
        }.Validate();

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain(mentions);
    }

    [Theory]
    [InlineData("sub/dir")]
    [InlineData(" ")]
    public void Ignore_patterns_must_be_single_names(string pattern)
        => new AppSettings { IgnorePatterns = [pattern] }.Validate().IsSuccess.Should().BeFalse();

    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    public void Slideshow_time_is_bounded(int seconds)
        => new AppSettings { SlideshowSeconds = seconds }.Validate().Error.Should().Contain("Slideshow");

    [Fact]
    public void Ffmpeg_folder_must_be_absolute()
        => new AppSettings { FfmpegFolder = "bin/ffmpeg" }.Validate().IsSuccess.Should().BeFalse();

    [Theory]
    [InlineData("Screenshots", true)]
    [InlineData("screenshots", true)]
    [InlineData("IMG_1_edited.jpg", true)]
    [InlineData("IMG_1.jpg", false)]
    [InlineData("Screenshots2", false)]
    public void Patterns_match_whole_names_with_wildcards(string name, bool ignored)
        => new AppSettings { IgnorePatterns = ["Screenshots", "*_edited.*"] }.IsIgnored(name).Should().Be(ignored);

    [Fact]
    public void Settings_compare_by_content()
    {
        var a = new AppSettings { IgnorePatterns = ["x"] };
        var b = new AppSettings { IgnorePatterns = new List<string> { "x" } };
        a.Should().Be(b);
        (a with { ThumbnailQuality = 90 }).Should().NotBe(b);
    }
}

public class CacheJanitorTests
{
    private readonly IThumbnailStore _store = Substitute.For<IThumbnailStore>();
    private readonly ISettingsStore _settings = Substitute.For<ISettingsStore>();
    private readonly CacheJanitor _janitor;

    public CacheJanitorTests()
    {
        _settings.Current.Returns(new AppSettings { PreviewCacheLimitMb = 2 });
        _store.TrimAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new CacheUsage(0, 0));
        _janitor = new CacheJanitor(_store, _settings, NullLogger<CacheJanitor>.Instance);
    }

    [Fact]
    public async Task Trims_only_previews_to_the_limit()
    {
        await _janitor.TrimAsync();

        await _store.Received(1).TrimAsync(ThumbnailFormat.Medium, 2L * 1024 * 1024, Arg.Any<CancellationToken>());
        await _store.DidNotReceive().TrimAsync(ThumbnailFormat.Small, Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_limit_means_no_trim()
    {
        _settings.Current.Returns(new AppSettings { PreviewCacheLimitMb = 0 });

        await _janitor.TrimAsync();

        await _store.DidNotReceiveWithAnyArgs().TrimAsync(default, default, default);
    }

    [Fact]
    public async Task Lowering_the_limit_trims_right_away()
    {
        var before = new AppSettings { PreviewCacheLimitMb = 1024 };
        var after = before with { PreviewCacheLimitMb = 2 };

        _settings.Changed += Raise.Event<Action<AppSettings, AppSettings>>(before, after);

        await SchedulerTests.WaitUntil(() => _store.ReceivedCalls().Any());
        await _store.Received(1).TrimAsync(ThumbnailFormat.Medium, 2L * 1024 * 1024, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Other_setting_changes_do_not_trim()
    {
        var before = new AppSettings();
        _settings.Changed += Raise.Event<Action<AppSettings, AppSettings>>(before, before with { ThumbnailQuality = 90 });

        await Task.Delay(50);
        _store.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_store_is_logged_not_thrown()
    {
        _store.TrimAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<Task<CacheUsage>>(_ => throw new IOException("disk gone"));

        (await _janitor.TrimAsync()).Should().Be(new CacheUsage(0, 0));
    }
}
