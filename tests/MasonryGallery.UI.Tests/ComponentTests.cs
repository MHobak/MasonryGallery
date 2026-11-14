namespace MasonryGallery.UI.Tests;

using Bunit;
using FluentAssertions;
using MasonryGallery.Application.Pipeline;
using MasonryGallery.Application.Ports;
using MasonryGallery.Application.Settings;
using MasonryGallery.Application.UseCases;
using MasonryGallery.Domain;
using MasonryGallery.Infrastructure.Persistence;
using MasonryGallery.Infrastructure.Settings;
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
    private readonly JsonSettingsStore _settings;
    private readonly WindowControl _window = new();
    private readonly List<bool> _fullScreen = [];

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
        _settings = new JsonSettingsStore(Path.Combine(_dir, "appsettings.json"), Path.Combine(_dir, "settings.json"),
            NullLogger<JsonSettingsStore>.Instance);
        Services.AddSingleton<ISettingsStore>(_settings);
        Services.AddSingleton<CacheJanitor>();
        Services.AddSingleton(new AppFolders(_dir, Path.Combine(_dir, "logs")));
        Services.AddSingleton<IClock, SystemClock>();
        Services.AddSingleton<GetLayoutItems>();
        Services.AddSingleton<RemoveLibrary>();
        Services.AddSingleton<ViewportHint>();
        Services.AddSingleton(new MediaEndpoint("http://127.0.0.1:1", "token"));
        Services.AddSingleton<FolderPicker>();
        Services.AddSingleton(_window);
        Services.AddSingleton<GalleryService>();

        // Module calls return null here; components must cope (zoom just isn't available).
        _window.SetFullScreen = _fullScreen.Add;
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/MasonryGallery.UI/js/lightbox.js").Mode = JSRuntimeMode.Loose;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private int Seed(string name, MediaKind kind = MediaKind.Image, bool? playable = null, SidecarData? sidecar = null)
    {
        _media.UpsertBatchAsync([new MediaItem
        {
            LibraryId = _library.Id, RelPath = "trip/" + name, FileName = name, Kind = kind, SizeBytes = 2048,
            ModifiedUtc = DateTime.UtcNow, DateTaken = DateTime.UtcNow, Width = 400, Height = 300,
            Playable = playable, ProbeState = ProbeState.Done, ScanId = 1,
            SidecarPath = sidecar is null ? null : "trip/" + name + ".xmp", SidecarModifiedUtc = sidecar is null ? null : DateTime.UtcNow
        }]).GetAwaiter().GetResult();
        if (sidecar is not null)
            _media.SaveSidecarsAsync(_library.Id.Value, [("trip/" + name, sidecar)]).GetAwaiter().GetResult();
        return _media.GetLayoutItemsAsync(new MediaFilter(Search: Path.GetFileNameWithoutExtension(name)))
            .GetAwaiter().GetResult().Single().Id;
    }

    private IRenderedComponent<Lightbox> OpenLightbox(int id, Action<(int, bool)>? onFavorite = null, Action<string>? onTag = null)
        => RenderComponent<Lightbox>(p => p
            .Add(c => c.Ids, new[] { id })
            .Add(c => c.CurrentId, id)
            .Add(c => c.Libraries, new[] { _library })
            .Add(c => c.OnFavoriteChanged, change => onFavorite?.Invoke(change))
            .Add(c => c.OnTagClicked, tag => onTag?.Invoke(tag)));

    [Fact]
    public void Presentation_goes_full_screen_keeps_arrow_keys_and_esc_leaves_it_first()
    {
        var ids = new[] { Seed("a.jpg"), Seed("b.jpg") };
        var closed = 0;
        var cut = RenderComponent<Lightbox>(p => p
            .Add(c => c.Ids, ids).Add(c => c.CurrentId, ids[0]).Add(c => c.Present, true)
            .Add(c => c.OnClose, _ => closed++));

        cut.WaitForAssertion(() => cut.Find(".lb").ClassList.Should().Contain("presenting"));
        _fullScreen.Should().Equal(true);
        cut.Find(".present-count").TextContent.Should().Be("1 / 2");

        cut.Find(".lb").KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });
        cut.WaitForAssertion(() => cut.Find(".present-count").TextContent.Should().Be("2 / 2"));

        cut.Find(".lb").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        cut.Find(".lb").ClassList.Should().NotContain("presenting");
        _fullScreen.Should().Equal(true, false);
        closed.Should().Be(0, "the first Esc only leaves presentation");

        cut.Find(".lb").KeyDown(new KeyboardEventArgs { Key = "p" });
        _fullScreen.Should().Equal(true, false, true);
        cut.Find(".lb").KeyDown(new KeyboardEventArgs { Key = "P" });
        cut.Find(".lb").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        closed.Should().Be(1);
    }

    [Fact]
    public void Gallery_re_render_keeps_the_item_the_viewer_moved_to()
    {
        var ids = new[] { Seed("a.jpg"), Seed("b.jpg"), Seed("c.jpg") };
        var cut = RenderComponent<Lightbox>(p => p.Add(c => c.Ids, ids).Add(c => c.CurrentId, ids[0]));
        cut.WaitForAssertion(() => cut.Find(".lb-title strong").TextContent.Should().Be("a.jpg"));

        cut.Find(".lb").KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });
        cut.WaitForAssertion(() => cut.Find(".lb-title strong").TextContent.Should().Be("b.jpg"));

        // The gallery re-renders (scan progress) and reloads its list in a new order; it still passes the item it opened.
        cut.SetParametersAndRender(p => p.Add(c => c.Ids, new[] { ids[2], ids[1], ids[0] }).Add(c => c.CurrentId, ids[0]));
        cut.Find(".lb-title strong").TextContent.Should().Be("b.jpg", "a re-render must not jump back to the opened item");

        cut.Find(".lb").KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });
        cut.WaitForAssertion(() => cut.Find(".lb-title strong").TextContent.Should().Be("a.jpg", "next follows the new order"));
    }

    [Fact]
    public async Task Swipes_move_through_items_toggle_details_and_close_like_esc()
    {
        var ids = new[] { Seed("a.jpg"), Seed("b.jpg") };
        var closed = 0;
        var cut = RenderComponent<Lightbox>(p => p
            .Add(c => c.Ids, ids).Add(c => c.CurrentId, ids[0]).Add(c => c.Present, true)
            .Add(c => c.OnClose, _ => closed++));
        cut.WaitForAssertion(() => cut.Find(".present-count").TextContent.Should().Be("1 / 2"));
        Task<bool> Swipe(string action) => cut.InvokeAsync(() => cut.Instance.OnSwipe(action));

        (await Swipe("prev")).Should().BeFalse("there is nothing before the first item, so it snaps back");
        (await Swipe("next")).Should().BeTrue();
        cut.Find(".present-count").TextContent.Should().Be("2 / 2");
        (await Swipe("next")).Should().BeFalse();

        (await Swipe("close")).Should().BeFalse("the first swipe down only leaves presentation");
        cut.Find(".lb").ClassList.Should().NotContain("presenting");
        closed.Should().Be(0);

        await Swipe("details");
        cut.FindAll(".lb-info").Should().ContainSingle();
        await Swipe("details");
        cut.FindAll(".lb-info").Should().BeEmpty();

        await Swipe("close");
        closed.Should().Be(1);
    }

    private IRenderedComponent<Lightbox> Present(int[] ids, bool autoplay)
        => RenderComponent<Lightbox>(p => p
            .Add(c => c.Ids, ids).Add(c => c.CurrentId, ids[0]).Add(c => c.Present, true).Add(c => c.Autoplay, autoplay));

    [Fact]
    public async Task Autoplay_moves_on_after_the_slide_time_and_wraps_around()
    {
        await _settings.SaveAsync(new AppSettings { SlideshowSeconds = 1 });
        var cut = Present([Seed("a.jpg"), Seed("b.jpg")], autoplay: true);

        cut.WaitForAssertion(() => cut.Find(".slide-progress"));
        cut.WaitForAssertion(() => cut.Find(".present-count").TextContent.Should().Be("2 / 2"), TimeSpan.FromSeconds(3));
        cut.WaitForAssertion(() => cut.Find(".present-count").TextContent.Should().Be("1 / 2"), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Paused_autoplay_stays_put_but_arrows_still_move()
    {
        await _settings.SaveAsync(new AppSettings { SlideshowSeconds = 1 });
        var cut = Present([Seed("a.jpg"), Seed("b.jpg")], autoplay: true);
        cut.WaitForAssertion(() => cut.Find(".slide-progress"));

        cut.Find(".lb").KeyDown(new KeyboardEventArgs { Key = "a" });
        cut.FindAll(".slide-progress").Should().BeEmpty();
        cut.Find("[aria-label=Autoplay]").GetAttribute("aria-pressed").Should().Be("false");
        await Task.Delay(1500);
        cut.Find(".present-count").TextContent.Should().Be("1 / 2");

        cut.Find(".lb").KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });
        cut.WaitForAssertion(() => cut.Find(".present-count").TextContent.Should().Be("2 / 2"));
    }

    [Fact]
    public void Videos_loop_in_the_viewer_but_not_in_presentation_unless_asked()
    {
        var ids = new[] { Seed("clip.mp4", MediaKind.Video, playable: true), Seed("b.jpg") };
        var cut = RenderComponent<Lightbox>(p => p.Add(c => c.Ids, ids).Add(c => c.CurrentId, ids[0]));
        cut.WaitForAssertion(() => cut.Find("video").HasAttribute("data-loop").Should().BeTrue());

        cut.Find(".lb").KeyDown(new KeyboardEventArgs { Key = "p" });
        cut.Find("video").HasAttribute("data-loop").Should().BeFalse("presentation plays videos through");
        cut.Find(".lb").KeyDown(new KeyboardEventArgs { Key = "r" });
        cut.Find("video").HasAttribute("data-loop").Should().BeTrue();

        // Autoplay waits for a looping video; once loop is off, the video's end moves on.
        cut.Find(".lb").KeyDown(new KeyboardEventArgs { Key = "a" });
        cut.FindAll(".slide-progress").Should().BeEmpty("videos are timed by their own length");
        cut.Find(".lb").KeyDown(new KeyboardEventArgs { Key = "R" });
        cut.Find("video").TriggerEvent("onended", EventArgs.Empty);
        cut.WaitForAssertion(() => cut.Find(".present-count").TextContent.Should().Be("2 / 2"));

        cut.Find(".lb").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        cut.Find(".lb").KeyDown(new KeyboardEventArgs { Key = "ArrowLeft" });
        cut.WaitForAssertion(() => cut.Find("video").HasAttribute("data-loop").Should().BeTrue("the viewer kept its own setting"));
    }

    [Fact]
    public void Closing_the_viewer_while_presenting_restores_the_window()
    {
        var id = Seed("a.jpg");
        var cut = RenderComponent<Lightbox>(p => p.Add(c => c.Ids, new[] { id }).Add(c => c.CurrentId, id).Add(c => c.Present, true));
        cut.WaitForAssertion(() => _fullScreen.Should().Equal(true));

        DisposeComponents();

        _fullScreen.Should().Equal(true, false);
    }

    [Fact]
    public void Viewer_shows_sidecar_details_and_grouped_read_only_tags()
    {
        var id = Seed("beach.jpg", sidecar: new SidecarData(
            [new SidecarTag("jane", "Artist"), new SidecarTag("sand", null), new SidecarTag("bob", "Character")],
            Creator: "jane", Source: "https://example.org/post/42", Rating: 3));
        string? clicked = null;
        var cut = OpenLightbox(id, onTag: tag => clicked = tag);
        cut.WaitForAssertion(() => cut.Find(".lb-title strong"));
        cut.Find(".lb").KeyDown(new KeyboardEventArgs { Key = "i" });

        cut.WaitForAssertion(() => cut.Find(".lb-info").TextContent.Should().Contain("example.org").And.Contain("★★★☆☆"));
        cut.FindAll(".lb-info h3").Select(h => h.TextContent).Should().Equal("Artist", "Character", "Keywords", "Your tags");
        cut.FindAll(".link-chip").Select(c => c.TextContent).Should().Equal("jane", "bob", "sand");
        cut.FindAll("button[aria-label^='Remove tag']").Should().BeEmpty("sidecar tags are edited in the sidecar");

        cut.FindAll(".link-chip").Single(c => c.TextContent == "bob").Click();
        clicked.Should().Be("bob");
    }

    [Fact]
    public async Task Tag_filter_suggests_and_builds_required_and_excluded_tags()
    {
        var id = Seed("beach.jpg");
        await _media.SetTagsAsync(id, ["long_hair", "smile"]);
        (IReadOnlyList<string> In, IReadOnlyList<string> Out) state = ([], []);
        IRenderedComponent<TagFilter> cut = null!;
        cut = RenderComponent<TagFilter>(p => p.Add(c => c.OnChanged, change =>
        {
            state = change;
            cut.SetParametersAndRender(q => q.Add(c => c.Included, change.Included).Add(c => c.Excluded, change.Excluded));
        }));

        cut.Find("input").Input("hair");
        cut.WaitForAssertion(() => cut.FindAll("[role=option]").Select(o => o.QuerySelector(".name")!.TextContent).Should().Equal("long_hair"));
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        // Key handlers are async; with the debounced search still settling, bUnit returns before they finish.
        cut.WaitForAssertion(() => state.In.Should().Equal("long_hair"));

        cut.Find("input").Input("-smile");
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        cut.WaitForAssertion(() => state.Out.Should().Equal("smile"));
        cut.FindAll(".tag-chip.off .name").Single().TextContent.Should().Be("not smile");

        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Backspace" });
        cut.WaitForAssertion(() => state.Out.Should().BeEmpty("backspace in an empty box removes the last chip"));
        cut.Find(".tag-chip.on .name").Click();
        cut.WaitForAssertion(() => state.Out.Should().Equal("long_hair"));
        state.In.Should().BeEmpty("clicking a required chip flips it to excluded");
    }

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

        cut.Find(".tag-chip button[aria-label^='Remove tag']").Click();
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

    [Fact]
    public async Task Library_panel_closes_with_escape()
    {
        var closed = 0;
        var libraries = await _libs.ListAsync();
        var cut = RenderComponent<LibraryPanel>(p => p
            .Add(c => c.Libraries, libraries)
            .Add(c => c.OnClose, () => closed++));

        cut.Find(".drawer").KeyDown(new KeyboardEventArgs { Key = "a" });
        closed.Should().Be(0);

        cut.Find(".drawer").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        closed.Should().Be(1);
    }

    [Fact]
    public void Settings_are_saved_and_invalid_input_is_explained()
    {
        var cut = RenderComponent<SettingsPanel>();
        cut.WaitForAssertion(() => cut.Find(".usage").TextContent.Should().Contain("Previews: 0 KB"));

        cut.Find("#quality").Input("90");
        cut.Find("#ignore").Change("Raw\n  *_edited.*  \n\n");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find("[role=status]").TextContent.Should().Be("Saved."));
        _settings.Current.ThumbnailQuality.Should().Be(90);
        _settings.Current.IgnorePatterns.Should().Equal("Raw", "*_edited.*");

        cut.Find("#ignore").Change("photos/raw");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find("[role=status]").ClassName.Should().Be("error"));
        cut.Find("[role=status]").TextContent.Should().Contain("slash");
        _settings.Current.IgnorePatterns.Should().Equal("Raw", "*_edited.*");
    }

    [Fact]
    public async Task Restore_defaults_only_fills_the_form()
    {
        await _settings.SaveAsync(new AppSettings { Workers = 3 });
        var cut = RenderComponent<SettingsPanel>();
        cut.Find("#workers").HasAttribute("disabled").Should().BeFalse();

        cut.FindAll(".actions button").Single(b => b.TextContent == "Restore defaults").Click();

        cut.Find("#workers").HasAttribute("disabled").Should().BeTrue("automatic again");
        _settings.Current.Workers.Should().Be(3, "nothing is applied until Save");
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
