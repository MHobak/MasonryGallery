namespace MasonryGallery.Infrastructure.Tests;

using System.Xml.Linq;
using FluentAssertions;
using MasonryGallery.Application.Pipeline;
using MasonryGallery.Application.Ports;
using MasonryGallery.Application.UseCases;
using MasonryGallery.Domain;
using MasonryGallery.Infrastructure.FileSystem;
using MasonryGallery.Infrastructure.Imaging;
using MasonryGallery.Infrastructure.Metadata;
using MasonryGallery.Infrastructure.Persistence;
using MasonryGallery.Infrastructure.Thumbnails;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

public sealed class SidecarTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mg-xmp-").FullName;
    private string Photos => Path.Combine(_dir, "photos");

    public SidecarTests() => Directory.CreateDirectory(Photos);

    public void Dispose() => Directory.Delete(_dir, true);

    // The shape gallery-dl + ExifTool write: dc:subject keywords, the same keywords again in digiKam and
    // Lightroom hierarchies (which carry the categories), and plain details.
    private static string Xmp(string[] subject, string[] hierarchical, string extra = "") => $"""
        <?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?>
        <x:xmpmeta xmlns:x='adobe:ns:meta/'>
        <rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>
         <rdf:Description rdf:about='' xmlns:dc='http://purl.org/dc/elements/1.1/'>
          <dc:creator><rdf:Seq><rdf:li>jane</rdf:li></rdf:Seq></dc:creator>
          <dc:source>https://example.org/post/42</dc:source>
          <dc:subject><rdf:Bag>{string.Concat(subject.Select(s => $"<rdf:li>{s}</rdf:li>"))}</rdf:Bag></dc:subject>
         </rdf:Description>
         <rdf:Description rdf:about='' xmlns:digiKam='http://www.digikam.org/ns/1.0/'>
          <digiKam:TagsList><rdf:Seq>{string.Concat(hierarchical.Select(s => $"<rdf:li>{s.Replace('|', '/')}</rdf:li>"))}<rdf:li>male-female</rdf:li></rdf:Seq></digiKam:TagsList>
         </rdf:Description>
         <rdf:Description rdf:about='' xmlns:lr='http://ns.adobe.com/lightroom/1.0/'>
          <lr:hierarchicalSubject><rdf:Bag>{string.Concat(hierarchical.Select(s => $"<rdf:li>{s}</rdf:li>"))}</rdf:Bag></lr:hierarchicalSubject>
         </rdf:Description>
         {extra}
        </rdf:RDF>
        </x:xmpmeta>
        <?xpacket end='w'?>
        """;

    private void WritePng(string rel)
    {
        using var bmp = new SKBitmap(40, 30);
        bmp.Erase(SKColors.Teal);
        using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(Photos, rel), data.ToArray());
        File.SetLastWriteTimeUtc(Path.Combine(Photos, rel), DateTime.UtcNow.AddMinutes(-10)); // past the settle time
    }

    private void WriteSidecar(string rel, string xml, DateTime? modifiedUtc = null)
    {
        var path = Path.Combine(Photos, rel);
        File.WriteAllText(path, xml);
        File.SetLastWriteTimeUtc(path, modifiedUtc ?? DateTime.UtcNow.AddMinutes(-5)); // past the settle time
    }

    [Fact]
    public void Keywords_take_their_category_from_hierarchies_and_details_are_read()
    {
        var doc = XDocument.Parse(Xmp(
            ["beach", "jane", "summer_(2024)", "male/female"],
            ["Artist|jane", "Copyright|summer_(2024)", "Character|bob"],
            """
            <rdf:Description rdf:about='' xmlns:xmp='http://ns.adobe.com/xap/1.0/' xmp:Rating='4'
                             xmlns:dc='http://purl.org/dc/elements/1.1/'>
             <xmp:CreateDate>2024-08-05T19:34:01+03:00</xmp:CreateDate>
             <dc:title><rdf:Alt><rdf:li xml:lang='de'>Strand</rdf:li><rdf:li xml:lang='x-default'>Beach day</rdf:li></rdf:Alt></dc:title>
            </rdf:Description>
            """));

        var data = XmpSidecarReader.Parse(doc);

        data.Tags.Should().Equal(
            new SidecarTag("beach", null), new SidecarTag("jane", "Artist"), new SidecarTag("summer_(2024)", "Copyright"),
            new SidecarTag("male/female", null), new SidecarTag("bob", "Character"));
        data.Tags.Should().NotContain(t => t.Name == "male-female", "digiKam's flat respelling of a dc:subject keyword");
        data.Creator.Should().Be("jane");
        data.Source.Should().Be("https://example.org/post/42");
        data.Title.Should().Be("Beach day");
        data.Rating.Should().Be(4);
        data.DateTaken.Should().Be(new DateTime(2024, 8, 5, 16, 34, 1, DateTimeKind.Utc));
    }

    [Fact]
    public void Without_dc_subject_flat_hierarchy_entries_are_the_keywords()
    {
        var doc = XDocument.Parse(Xmp([], ["People|Family|Mom"]).Replace("<dc:subject><rdf:Bag></rdf:Bag></dc:subject>", ""));

        XmpSidecarReader.Parse(doc).Tags.Should().Equal(new SidecarTag("Mom", "People/Family"), new SidecarTag("male-female", null));
    }

    [Theory]
    [InlineData("<x:xmpmeta xmlns:x='adobe:ns:meta/'><unclosed>")]
    [InlineData("<!DOCTYPE x [<!ENTITY a 'aaaa'>]><x>&a;</x>")] // entity expansion is refused, not evaluated
    public async Task Broken_or_hostile_sidecars_fail_cleanly(string xml)
    {
        WriteSidecar("bad.xmp", xml);

        var result = await new XmpSidecarReader(NullLogger<XmpSidecarReader>.Instance).ReadAsync(Path.Combine(Photos, "bad.xmp"));

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Enumerator_pairs_both_sidecar_styles_and_finds_their_owners()
    {
        WritePng("a.png");
        WriteSidecar("a.png.xmp", Xmp(["x"], []));
        WritePng("b.png");
        WriteSidecar("b.xmp", Xmp(["y"], []));
        WritePng("c.png");
        var enumerator = new FileSystemEnumerator(NullLogger<FileSystemEnumerator>.Instance);

        var entries = new List<FileEntry>();
        await foreach (var e in enumerator.EnumerateAsync(Photos, followSymlinks: false))
            entries.Add(e);

        entries.OrderBy(e => e.RelativePath).Select(e => (e.RelativePath, e.SidecarPath))
            .Should().Equal(("a.png", "a.png.xmp"), ("b.png", "b.xmp"), ("c.png", null));
        enumerator.Inspect(Photos, "b.png", false).File!.SidecarPath.Should().Be("b.xmp");
        enumerator.FindSidecarOwners(Photos, "a.png.xmp").Should().Equal("a.png");
        enumerator.FindSidecarOwners(Photos, "b.xmp").Should().Equal("b.png");
        enumerator.FindSidecarOwners(Photos, "a.png").Should().BeEmpty();
    }

    [Fact]
    public async Task Scans_import_sidecars_and_follow_their_changes_without_touching_user_tags()
    {
        WritePng("a.png");
        WritePng("b.png");
        WriteSidecar("a.png.xmp", Xmp(["beach", "jane"], ["Artist|jane"]));
        var rig = await Rig.CreateAsync(_dir);
        await rig.ScanAsync();
        var a = (await rig.Media.GetLayoutItemsAsync(new MediaFilter(Search: "a"))).Single().Id;
        var b = (await rig.Media.GetLayoutItemsAsync(new MediaFilter(Search: "b"))).Single().Id;

        (await rig.Media.GetTagsForMediaAsync(a)).Select(t => (t.Name, t.Category, t.FromSidecar))
            .Should().Equal(("beach", null, true), ("jane", "Artist", true));
        (await rig.Media.GetSidecarAsync(a))!.Source.Should().Be("https://example.org/post/42");
        (await rig.Media.GetSidecarAsync(b)).Should().BeNull();

        // The user's own tag survives every sidecar change.
        await rig.Media.SetTagsAsync(a, ["keeper"]);
        WriteSidecar("a.png.xmp", Xmp(["sunset"], []), DateTime.UtcNow.AddMinutes(-1));
        await rig.ScanAsync();
        (await rig.Media.GetTagsForMediaAsync(a)).Select(t => t.Name).Should().Equal("keeper", "sunset");
        (await rig.Media.GetByIdAsync(a))!.ThumbState.Should().Be(ThumbState.Done, "a sidecar edit doesn't change the picture");

        // The watcher reports the sidecar path itself; the media file is what gets re-read.
        File.Delete(Path.Combine(Photos, "a.png.xmp"));
        (await rig.Scan.ExecutePathsAsync(rig.Library.Id, ["a.png.xmp"])).IsSuccess.Should().BeTrue();
        (await rig.Media.GetTagsForMediaAsync(a)).Select(t => t.Name).Should().Equal("keeper");
        (await rig.Media.GetSidecarAsync(a)).Should().BeNull();
        (await rig.Media.SearchTagsAsync("sun", 10)).Should().BeEmpty("tags nothing uses any more are dropped");
    }

    [Fact]
    public async Task Sidecar_date_fills_in_for_files_without_exif()
    {
        WritePng("a.png");
        WriteSidecar("a.png.xmp", Xmp(["x"], [], """
            <rdf:Description rdf:about='' xmlns:xmp='http://ns.adobe.com/xap/1.0/'><xmp:CreateDate>2020-01-02T03:04:05Z</xmp:CreateDate></rdf:Description>
            """));
        var rig = await Rig.CreateAsync(_dir);

        await rig.ScanAsync();

        var item = await rig.Media.GetByIdAsync((await rig.Media.GetLayoutItemsAsync(new MediaFilter())).Single().Id);
        item!.DateTaken.Should().Be(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Tag_filters_require_and_exclude_and_search_suggests_by_word()
    {
        foreach (var (name, tags) in new[] { ("a", new[] { "long_hair", "smile" }), ("b", ["long_hair", "hairband"]), ("c", ["short_hair", "smile"]) })
        {
            WritePng($"{name}.png");
            WriteSidecar($"{name}.png.xmp", Xmp(tags, []));
        }
        var rig = await Rig.CreateAsync(_dir);
        await rig.ScanAsync();

        async Task<string[]> NamesAsync(MediaFilter filter)
        {
            var ids = (await rig.Media.GetLayoutItemsAsync(filter with { Sort = MediaSort.Name })).Select(i => i.Id).ToList();
            var names = await rig.Media.GetFileNamesAsync(ids);
            return ids.Select(id => names[id]).ToArray();
        }

        (await NamesAsync(new MediaFilter(Tags: ["LONG_HAIR"]))).Should().Equal("a.png", "b.png");
        (await NamesAsync(new MediaFilter(Tags: ["long_hair", "smile"]))).Should().Equal("a.png");
        (await NamesAsync(new MediaFilter(Tags: ["smile"], ExcludedTags: ["long_hair"]))).Should().Equal("c.png");
        (await NamesAsync(new MediaFilter(Tags: ["unknown"]))).Should().BeEmpty();

        (await rig.Media.SearchTagsAsync("hair", 10)).Select(t => (t.Name, t.Count))
            .Should().Equal([("hairband", 1), ("long_hair", 2), ("short_hair", 1)], "names starting with the text come first, then by use");
        (await rig.Media.SearchTagsAsync("S", 10)).Select(t => t.Name).Should().Equal("smile", "short_hair");
        (await rig.Media.SearchTagsAsync("%", 10)).Should().BeEmpty("LIKE wildcards are matched literally");
    }

    private sealed record Rig(SqliteMediaRepository Media, Scan Scan, Library Library)
    {
        public static async Task<Rig> CreateAsync(string dir)
        {
            var factory = new SqliteConnectionFactory(Path.Combine(dir, "t.db"), NullLogger<SqliteConnectionFactory>.Instance);
            await SchemaMigrator.MigrateAsync(factory, NullLogger.Instance);
            var libs = new SqliteLibraryRepository(factory, NullLogger<SqliteLibraryRepository>.Instance);
            var media = new SqliteMediaRepository(factory, NullLogger<SqliteMediaRepository>.Instance);
            var scan = new Scan(libs, media,
                new FileSystemEnumerator(NullLogger<FileSystemEnumerator>.Instance),
                new SkiaProbe(NullLogger<SkiaProbe>.Instance),
                new SkiaThumbnailGenerator(NullLogger<SkiaThumbnailGenerator>.Instance),
                new ShardedThumbnailStore(Path.Combine(dir, "thumbs"), "v2", NullLogger<ShardedThumbnailStore>.Instance),
                new SystemClock(), new PipelineProgressReporter(), NullLogger<Scan>.Instance,
                sidecars: new XmpSidecarReader(NullLogger<XmpSidecarReader>.Instance));
            var library = (await new AddLibrary(libs).ExecuteAsync("Photos", Path.Combine(dir, "photos"))).Value!;
            return new Rig(media, scan, library);
        }

        public async Task ScanAsync() => (await Scan.ExecuteAsync(Library.Id)).Error.Should().BeNull();
    }
}
