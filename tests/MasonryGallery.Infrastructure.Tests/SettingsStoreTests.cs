namespace MasonryGallery.Infrastructure.Tests;

using System.Text.Json.Nodes;
using FluentAssertions;
using MasonryGallery.Application.Ports;
using MasonryGallery.Application.Settings;
using MasonryGallery.Domain;
using MasonryGallery.Infrastructure.FileSystem;
using MasonryGallery.Infrastructure.Settings;
using MasonryGallery.Infrastructure.Thumbnails;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

internal sealed class FixedSettings(AppSettings settings) : ISettingsStore
{
    public AppSettings Current => settings;
    public AppSettings Defaults => new();
    public event Action<AppSettings, AppSettings>? Changed { add { } remove { } }
    public Task<Result> SaveAsync(AppSettings s, CancellationToken ct = default) => throw new NotSupportedException();
}

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mg-settings-").FullName;
    private string DefaultsPath => Path.Combine(_dir, "appsettings.json");
    private string UserPath => Path.Combine(_dir, "settings.json");

    public void Dispose() => Directory.Delete(_dir, true);

    private JsonSettingsStore Open() => new(DefaultsPath, UserPath, NullLogger<JsonSettingsStore>.Instance);

    [Fact]
    public void Without_files_the_built_in_defaults_apply()
        => Open().Current.Should().Be(new AppSettings());

    [Fact]
    public void User_values_override_shipped_defaults_one_at_a_time()
    {
        File.WriteAllText(DefaultsPath, """
            {
              // comments are allowed, like in any appsettings.json
              "Logging": { "LogLevel": { "Default": "Warning" } },
              "Gallery": { "ThumbnailQuality": 70, "PreviewCacheLimitMb": 512, }
            }
            """);
        File.WriteAllText(UserPath, """{ "previewCacheLimitMb": 64, "IgnorePatterns": ["Raw"] }""");

        var store = Open();

        store.Defaults.ThumbnailQuality.Should().Be(70);
        store.Current.ThumbnailQuality.Should().Be(70);
        store.Current.PreviewCacheLimitMb.Should().Be(64);
        store.Current.IgnorePatterns.Should().Equal("Raw");
    }

    [Fact]
    public async Task Saving_writes_only_what_differs_from_the_defaults()
    {
        var store = Open();
        (AppSettings Before, AppSettings After)? changed = null;
        store.Changed += (before, after) => changed = (before, after);

        var result = await store.SaveAsync(new AppSettings { Workers = 3, IgnorePatterns = ["Raw"] });

        result.IsSuccess.Should().BeTrue();
        var saved = JsonNode.Parse(File.ReadAllText(UserPath))!.AsObject();
        saved.Select(p => p.Key).Should().BeEquivalentTo("Workers", "IgnorePatterns");
        Open().Current.Should().Be(store.Current, "a restart reads back what was saved");
        changed!.Value.After.Workers.Should().Be(3);
    }

    [Fact]
    public async Task Invalid_settings_are_rejected_and_nothing_is_written()
    {
        var store = Open();

        var result = await store.SaveAsync(new AppSettings { ThumbnailQuality = 10 });

        result.IsSuccess.Should().BeFalse();
        File.Exists(UserPath).Should().BeFalse();
        store.Current.Should().Be(new AppSettings());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "ThumbnailQuality": 5 }""")]
    [InlineData("""{ "Workers": "many" }""")]
    public void A_broken_user_file_falls_back_to_defaults_and_is_kept(string content)
    {
        File.WriteAllText(UserPath, content);

        Open().Current.Should().Be(new AppSettings());
        File.ReadAllText(UserPath).Should().Be(content, "a hand-edit with a typo must not be thrown away");
    }

    [Fact]
    public async Task Preview_trim_deletes_least_recently_used_first()
    {
        var store = new ShardedThumbnailStore(Path.Combine(_dir, "thumbs"), "v2", NullLogger<ShardedThumbnailStore>.Instance);
        var old = DateTime.UtcNow.AddDays(-10);
        for (var id = 1; id <= 4; id++)
        {
            await store.SaveAsync(id, 960, new byte[1000]);
            var path = store.GetPath(id, 960);
            File.SetLastWriteTimeUtc(path, old);
            File.SetLastAccessTimeUtc(path, old.AddDays(id)); // 1 is the least recently viewed
        }
        await store.SaveAsync(9, 320, new byte[1000]);

        var removed = await store.TrimAsync(960, maxBytes: 3000);

        removed.Should().Be(new CacheUsage(2, 2000), "trimming goes to 90% of the cap: 2700 bytes");
        File.Exists(store.GetPath(1, 960)).Should().BeFalse();
        File.Exists(store.GetPath(2, 960)).Should().BeFalse();
        File.Exists(store.GetPath(3, 960)).Should().BeTrue();
        File.Exists(store.GetPath(9, 320)).Should().BeTrue("grid thumbnails are a different size");
        (await store.GetUsageAsync(960)).Should().Be(new CacheUsage(2, 2000));
        (await store.TrimAsync(960, maxBytes: 3000)).Files.Should().Be(0, "already under the cap");
    }

    [Fact]
    public async Task Ignore_patterns_apply_to_walks_and_single_paths()
    {
        var lib = Path.Combine(_dir, "lib");
        foreach (var rel in new[] { "a.jpg", "a_edited.jpg", "Raw/b.jpg", "trip/raw/c.jpg", "trip/d.jpg" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(lib, rel))!);
            File.WriteAllText(Path.Combine(lib, rel), "x");
        }
        var settings = new FixedSettings(new AppSettings { IgnorePatterns = ["raw", "*_edited.*"] });
        var enumerator = new FileSystemEnumerator(NullLogger<FileSystemEnumerator>.Instance, settings);

        var found = new List<string>();
        await foreach (var e in enumerator.EnumerateAsync(lib, followSymlinks: false))
            found.Add(e.RelativePath.Replace('\\', '/'));

        found.Order().Should().Equal("a.jpg", "trip/d.jpg");
        enumerator.Inspect(lib, "trip/raw/c.jpg", false).Kind.Should().Be(PathKind.Ignored);
        enumerator.Inspect(lib, "a_edited.jpg", false).Kind.Should().Be(PathKind.Ignored);
        enumerator.Inspect(lib, "trip/d.jpg", false).Kind.Should().Be(PathKind.File);
    }
}
