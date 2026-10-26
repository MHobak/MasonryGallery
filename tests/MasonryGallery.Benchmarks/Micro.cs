namespace MasonryGallery.Benchmarks;

using BenchmarkDotNet.Attributes;
using MasonryGallery.Application.Pipeline;
using MasonryGallery.Application.Ports;
using MasonryGallery.Domain;
using MasonryGallery.Infrastructure.FileSystem;
using MasonryGallery.Infrastructure.Imaging;
using MasonryGallery.Infrastructure.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

/// <summary>The per-file work of a scan: walking, reading a sidecar, making a thumbnail. Self-contained fixtures.</summary>
[MemoryDiagnoser]
public class Micro
{
    private string _dir = "";
    private string _photo = "", _sidecar = "";
    private readonly FileSystemEnumerator _enumerator = new(NullLogger<FileSystemEnumerator>.Instance);
    private readonly XmpSidecarReader _xmp = new(NullLogger<XmpSidecarReader>.Instance);
    private readonly SkiaThumbnailGenerator _thumbnails = new(NullLogger<SkiaThumbnailGenerator>.Instance);

    [GlobalSetup]
    public void Setup()
    {
        _dir = Directory.CreateTempSubdirectory("mg-bench-").FullName;
        var stub = SKImage.FromBitmap(new SKBitmap(16, 12)).Encode(SKEncodedImageFormat.Jpeg, 70).ToArray();
        for (var i = 0; i < 10_000; i++)
        {
            var folder = Path.Combine(_dir, "walk", $"event-{i / 500:D2}");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, $"IMG_{i:D5}.jpg"), stub);
        }

        using (var bmp = new SKBitmap(4000, 3000))
        {
            bmp.Erase(SKColors.SteelBlue);
            _photo = Path.Combine(_dir, "photo.jpg");
            File.WriteAllBytes(_photo, SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, 90).ToArray());
        }

        // The shape gallery-dl writes: ~60 keywords, repeated in two hierarchies with categories.
        var keywords = Enumerable.Range(0, 60).Select(i => $"keyword_{i}").ToList();
        _sidecar = Path.Combine(_dir, "photo.jpg.xmp");
        File.WriteAllText(_sidecar, $"""
            <x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>
             <rdf:Description rdf:about='' xmlns:dc='http://purl.org/dc/elements/1.1/'>
              <dc:creator><rdf:Seq><rdf:li>someone</rdf:li></rdf:Seq></dc:creator>
              <dc:source>https://example.org/post/1</dc:source>
              <dc:subject><rdf:Bag>{string.Concat(keywords.Select(k => $"<rdf:li>{k}</rdf:li>"))}</rdf:Bag></dc:subject>
             </rdf:Description>
             <rdf:Description rdf:about='' xmlns:digiKam='http://www.digikam.org/ns/1.0/'>
              <digiKam:TagsList><rdf:Seq>{string.Concat(keywords.Select(k => $"<rdf:li>Group/{k}</rdf:li>"))}</rdf:Seq></digiKam:TagsList>
             </rdf:Description>
             <rdf:Description rdf:about='' xmlns:lr='http://ns.adobe.com/lightroom/1.0/'>
              <lr:hierarchicalSubject><rdf:Bag>{string.Concat(keywords.Select(k => $"<rdf:li>Group|{k}</rdf:li>"))}</rdf:Bag></lr:hierarchicalSubject>
             </rdf:Description>
            </rdf:RDF></x:xmpmeta>
            """);
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_dir, true);

    [Benchmark(Description = "Walk 10k files in 20 folders")]
    public async Task<int> Walk()
    {
        var n = 0;
        await foreach (var _ in _enumerator.EnumerateAsync(Path.Combine(_dir, "walk"), followSymlinks: false))
            n++;
        return n;
    }

    [Benchmark(Description = "Read an XMP sidecar (60 keywords)")]
    public async Task<int> ReadSidecar() => (await _xmp.ReadAsync(_sidecar)).Value!.Tags.Count;

    [Benchmark(Description = "Thumbnail of a 12 MP JPEG")]
    public async Task<int> Thumbnail()
        => (await _thumbnails.GenerateSmallAsync(new ThumbnailJob(1, _photo, MediaKind.Image, ThumbnailFormat.Version))).Value!.Webp.Length;
}
