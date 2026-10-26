namespace MasonryGallery.Infrastructure.Metadata;

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using MasonryGallery.Application.Ports;
using MasonryGallery.Domain;
using Microsoft.Extensions.Logging;

/// <summary>
/// Reads the parts of an XMP sidecar the gallery uses: keywords (flat and hierarchical), creator, source,
/// title, description, rating and capture date. Written by digiKam, darktable, Lightroom, ExifTool, gallery-dl.
/// </summary>
public sealed class XmpSidecarReader : ISidecarReader
{
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace Lightroom = "http://ns.adobe.com/lightroom/1.0/";
    private static readonly XNamespace DigiKam = "http://www.digikam.org/ns/1.0/";
    private static readonly XNamespace MicrosoftPhoto = "http://ns.microsoft.com/photo/1.0/";
    private static readonly XNamespace Photoshop = "http://ns.adobe.com/photoshop/1.0/";
    private static readonly XNamespace Exif = "http://ns.adobe.com/exif/1.0/";

    // Sidecars are untrusted input: no DTDs (entity expansion), no external resources, bounded size.
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 16 * 1024 * 1024,
        Async = true,
    };

    private readonly ILogger<XmpSidecarReader> _logger;

    public XmpSidecarReader(ILogger<XmpSidecarReader> logger) => _logger = logger;

    public async ValueTask<Result<SidecarData>> ReadAsync(string sidecarPath, CancellationToken ct = default)
    {
        try
        {
            await using var stream = new FileStream(sidecarPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = XmlReader.Create(stream, ReaderSettings);
            var doc = await XDocument.LoadAsync(reader, LoadOptions.None, ct);
            return Parse(doc);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Unreadable sidecar {Path}", sidecarPath);
            return Result<SidecarData>.Failure(ex.Message);
        }
    }

    internal static SidecarData Parse(XDocument doc)
    {
        var tags = new List<SidecarTag>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string? category)
        {
            name = name.Trim();
            if (name.Length == 0)
                return;
            if (index.TryGetValue(name, out var at))
            {
                if (tags[at].Category is null && category is not null)
                    tags[at] = tags[at] with { Category = category };
                return;
            }
            index[name] = tags.Count;
            tags.Add(new SidecarTag(name, category));
        }

        // dc:subject holds the keywords as written. Hierarchical lists repeat them with a path
        // ("Artist|name"), which supplies the category; their flat entries are spelled differently
        // at times (digiKam can't store "/" in a name), so they count only when dc:subject is empty.
        var subject = Values(doc, Dc + "subject").ToList();
        foreach (var keyword in subject)
            Add(keyword, null);
        var hierarchies = new[] { (Lightroom + "hierarchicalSubject", '|'), (DigiKam + "TagsList", '/'), (MicrosoftPhoto + "LastKeywordXMP", '/') };
        foreach (var (name, separator) in hierarchies)
        {
            foreach (var path in Values(doc, name))
            {
                var parts = path.Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                    Add(parts[^1], string.Join('/', parts[..^1]));
                else if (parts.Length == 1 && subject.Count == 0)
                    Add(parts[0], null);
            }
        }

        var creators = Values(doc, Dc + "creator").ToList();
        return new SidecarData(
            tags,
            Creator: creators.Count > 0 ? string.Join(", ", creators) : null,
            Source: Values(doc, Dc + "source").FirstOrDefault(),
            Title: Alternative(doc, Dc + "title"),
            Description: Alternative(doc, Dc + "description"),
            Rating: int.TryParse(Values(doc, Xmp + "Rating").FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rating)
                    && rating is >= 1 and <= 5 ? rating : null, // 0 = unrated, -1 = rejected
            DateTaken: new[] { Exif + "DateTimeOriginal", Photoshop + "DateCreated", Xmp + "CreateDate" }
                .Select(n => ParseDate(Values(doc, n).FirstOrDefault()))
                .FirstOrDefault(d => d is not null));
    }

    // A property is written either as an attribute of rdf:Description or as an element holding
    // text or an rdf:Bag/Seq/Alt of rdf:li items.
    private static IEnumerable<string> Values(XDocument doc, XName property)
    {
        foreach (var attribute in doc.Descendants(Rdf + "Description").Attributes(property))
            yield return attribute.Value.Trim();
        foreach (var element in doc.Descendants(property))
        {
            var items = element.Elements().Where(e => e.Name.Namespace == Rdf).Elements(Rdf + "li").ToList();
            if (items.Count > 0)
            {
                foreach (var li in items)
                {
                    if (li.Value.Trim() is { Length: > 0 } text)
                        yield return text;
                }
            }
            else if (element.Value.Trim() is { Length: > 0 } text)
            {
                yield return text;
            }
        }
    }

    // Language alternatives: the default language if marked, else the first.
    private static string? Alternative(XDocument doc, XName property)
    {
        var element = doc.Descendants(property).FirstOrDefault();
        var preferred = element?.Descendants(Rdf + "li")
            .FirstOrDefault(li => (string?)li.Attribute(XNamespace.Xml + "lang") == "x-default");
        var text = preferred?.Value ?? Values(doc, property).FirstOrDefault();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    // XMP dates are ISO 8601, sometimes without a zone or time. Without a zone, local time is assumed,
    // as ExifReader does for EXIF dates.
    private static DateTime? ParseDate(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed.UtcDateTime
            : null;
}
