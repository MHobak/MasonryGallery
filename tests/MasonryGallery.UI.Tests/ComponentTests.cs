namespace MasonryGallery.UI.Tests;

using Bunit;
using FluentAssertions;
using MasonryGallery.Application.Pipeline;
using MasonryGallery.Application.Ports;
using MasonryGallery.Application.UseCases;
using MasonryGallery.Domain;
using MasonryGallery.Infrastructure.Persistence;
using MasonryGallery.Infrastructure.Thumbnails;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class ComponentTests : TestContext
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mg-ui-").FullName;
    private readonly SqliteMediaRepository _media;
    private readonly SqliteLibraryRepository _libs;
    private readonly Library _library;

    public ComponentTests()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_dir, "t.db"), NullLogger<SqliteConnectionFactory>.Instance);
        SchemaMigrator.MigrateAsync(factory, NullLogger.Instance).GetAwaiter().GetResult();
        _libs = new SqliteLibraryRepository(factory, NullLogger<SqliteLibraryRepository>.Instance);
        _media = new SqliteMediaRepository(factory, NullLogger<SqliteMediaRepository>.Instance);
        _library = _libs.AddAsync(new Library("Photos", Path.Combine(_dir, "photos"))).GetAwaiter().GetResult().Value!;

        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddSingleton<IMediaRepository>(_media);
        Services.AddSingleton<ILibraryRepository>(_libs);
        Services.AddSingleton<IThumbnailStore>(
            new ShardedThumbnailStore(Path.Combine(_dir, "thumbs"), "v2", NullLogger<ShardedThumbnailStore>.Instance));
        Services.AddSingleton<IClock, SystemClock>();
        Services.AddSingleton<GetLayoutItems>();
        Services.AddSingleton<RemoveLibrary>();
        Services.AddSingleton<ViewportHint>();
        Services.AddSingleton(new MediaEndpoint("http://127.0.0.1:1", "token"));
        Services.AddSingleton<FolderPicker>();
        Services.AddSingleton<GalleryService>();

        // Module calls return null here; components must cope (zoom just isn't available).
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/MasonryGallery.UI/js/lightbox.js").Mode = JSRuntimeMode.Loose;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private int Seed(string name, MediaKind kind = MediaKind.Image, bool? playable = null)
    {
        _media.UpsertBatchAsync([new MediaItem
        {
            LibraryId = _library.Id, RelPath = "trip/" + name, FileName = name, Kind = kind, SizeBytes = 2048,
            ModifiedUtc = DateTime.UtcNow, DateTaken = DateTime.UtcNow, Width = 400, Height = 300,
            Playable = playable, ProbeState = ProbeState.Done, ScanId = 1
        }]).GetAwaiter().GetResult();
        return _media.GetLayoutItemsAsync(new MediaFilter(Search: Path.GetFileNameWithoutExtension(name)))
            .GetAwaiter().GetResult().Single().Id;
    }

    private IRenderedComponent<Lightbox> OpenLightbox(int id, Action<(int, bool)>? onFavorite = null)
        => RenderComponent<Lightbox>(p => p
            .Add(c => c.Ids, new[] { id })
            .Add(c => c.CurrentId, id)
            .Add(c => c.Libraries, new[] { _library })
            .Add(c => c.OnFavoriteChanged, change => onFavorite?.Invoke(change)));

    [Fact]
    public void Lightbox_keys_toggle_favorite_and_details()
    {
        var id = Seed("beach.jpg");
        (int, bool)? reported = null;
        var cut = OpenLightbox(id, change => reported = change);
        cut.WaitForAssertion(() => cut.Find(".lb-title strong").TextContent.Should().Be("beach.jpg"));

        cut.Find(".lb").KeyDown(new KeyboardEventArgs { Key = "f" });
        cut.WaitForAssertion(() => _media.GetByIdAsync(id).Result!.Favorite.Should().BeTrue());
        reported.Should().Be((id, true));
        cut.Find(".fav-btn").GetAttribute("aria-pressed").Should().Be("true");

        cut.Find(".lb").KeyDown(new KeyboardEventArgs { Key = "i" });
        cut.Find(".lb-info").TextContent.Should().Contain("Photos").And.Contain("trip").And.Contain("400 × 300");
    }

    [Fact]
    public async Task Tags_are_added_with_enter_and_removed_with_the_chip_button()
    {
        var id = Seed("beach.jpg");
        var cut = OpenLightbox(id);
        cut.Find(".lb").KeyDown(new KeyboardEventArgs { Key = "i" });

        var input = cut.Find(".tag-input");
        input.Input("Summer");
        input.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        cut.WaitForAssertion(() => cut.FindAll(".tag-chip").Should().ContainSingle().Which.TextContent.Should().Contain("Summer"));
        (await _media.GetTagsForMediaAsync(id)).Select(t => t.Name).Should().Equal("Summer");

        cut.Find(".tag-chip button").Click();
        cut.WaitForAssertion(() => cut.FindAll(".tag-chip").Should().BeEmpty());
        (await _media.GetTagsForMediaAsync(id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Typing_in_the_tag_box_does_not_trigger_shortcuts()
    {
        var id = Seed("beach.jpg");
        var cut = OpenLightbox(id);
        cut.Find(".lb").KeyDown(new KeyboardEventArgs { Key = "i" });

        cut.Find(".tag-input").KeyDown(new KeyboardEventArgs { Key = "f" });

        (await _media.GetByIdAsync(id))!.Favorite.Should().BeFalse();
        cut.FindAll(".lb-info").Should().ContainSingle("'i' typed into the box must not close the panel either");
    }

    [Fact]
    public void Unplayable_video_offers_the_default_player_and_can_be_tried_anyway()
    {
        var id = Seed("clip.avi", MediaKind.Video, playable: false);
        var cut = OpenLightbox(id);

        cut.WaitForAssertion(() => cut.Find(".lb-fallback").TextContent.Should().Contain("usually can't be played"));
        cut.FindAll("video").Should().BeEmpty();

        cut.FindAll(".lb-fallback button").Single(b => b.TextContent.Contains("Try here anyway")).Click();
        cut.Find("video").GetAttribute("src").Should().Contain($"/media/{id}?t=token");
    }

    [Fact]
    public void Video_that_fails_to_load_falls_back()
    {
        var id = Seed("clip.mp4", MediaKind.Video, playable: true);
        var cut = OpenLightbox(id);

        cut.WaitForAssertion(() => cut.Find("video"));
        cut.Find("video").TriggerEvent("onerror", new ErrorEventArgs());

        cut.Find(".lb-fallback").TextContent.Should().Contain("can't be played here");
        cut.FindAll(".lb-fallback button").Should().ContainSingle("no 'try anyway' after a real failure");
    }

    [Fact]
    public async Task Library_is_removed_only_after_confirming()
    {
        Seed("beach.jpg");
        var changed = 0;
        var libraries = await _libs.ListAsync();
        var cut = RenderComponent<LibraryPanel>(p => p
            .Add(c => c.Libraries, libraries)
            .Add(c => c.OnChanged, () => changed++));

        cut.FindAll(".lib-actions button").Single(b => b.TextContent == "Remove…").Click();
        cut.Find(".libs .hint").TextContent.Should().Contain("Your files are not touched");
        (await _libs.ListAsync()).Should().HaveCount(1);

        cut.FindAll(".lib-actions button").Single(b => b.TextContent == "Remove").Click();

        cut.WaitForAssertion(() => _libs.ListAsync().Result.Should().BeEmpty());
        changed.Should().Be(1);
    }

    [Theory]
    [InlineData(WatchMode.Live, null, "Watching for changes")]
    [InlineData(WatchMode.Polling, "Network folder: changes are picked up every few minutes.", "Network folder")]
    public async Task Library_shows_how_it_is_kept_current(WatchMode mode, string? reason, string expected)
    {
        var libraries = await _libs.ListAsync();
        var statuses = new Dictionary<LibraryId, WatchStatus> { [_library.Id] = new(mode, reason) };

        var cut = RenderComponent<LibraryPanel>(p => p
            .Add(c => c.Libraries, libraries)
            .Add(c => c.WatchStatuses, statuses));

        cut.Find(".lib-info .watch").TextContent.Should().Contain(expected);
    }

    [Fact]
    public async Task Problems_section_lists_unreadable_files()
    {
        var id = Seed("broken.jpg");
        await _media.UpdateThumbAsync(id, ThumbState.Failed, 2);
        var libraries = await _libs.ListAsync();

        var cut = RenderComponent<LibraryPanel>(p => p.Add(c => c.Libraries, libraries));

        cut.WaitForAssertion(() => cut.Find(".problems").TextContent.Should().Contain("broken.jpg").And.Contain("no preview"));
    }
}
