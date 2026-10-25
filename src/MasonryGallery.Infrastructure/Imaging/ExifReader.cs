namespace MasonryGallery.Infrastructure.Imaging;

using System.Buffers.Binary;
using System.Globalization;

/// <summary>Reads the capture date from a JPEG's EXIF block, touching only the start of the file.</summary>
internal static class ExifReader
{
    // APP1 (EXIF) is at most 64 KB and sits right after SOI, possibly behind APP0/JFIF.
    private const int HeaderBytes = 128 * 1024;

    private const ushort TagDateTime = 0x0132;
    private const ushort TagExifIfd = 0x8769;
    private const ushort TagDateTimeOriginal = 0x9003;
    private const ushort TagDateTimeDigitized = 0x9004;

    public static DateTime? ReadDateTaken(string path)
    {
        var buffer = new byte[HeaderBytes];
        int read;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1))
            read = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return ReadDateTaken(buffer.AsSpan(0, read));
    }

    /// <summary>DateTimeOriginal, else DateTimeDigitized, else IFD0 DateTime. EXIF has no time zone; local time is assumed.</summary>
    public static DateTime? ReadDateTaken(ReadOnlySpan<byte> jpeg)
    {
        var tiff = FindTiff(jpeg);
        if (tiff.Length < 8 || tiff[0] != tiff[1] || (tiff[0] != 'I' && tiff[0] != 'M'))
            return null;

        var little = tiff[0] == 'I';
        var ifd0 = U32(tiff, 4, little);
        DateTime? taken = null;
        if (TryFindEntry(tiff, ifd0, TagExifIfd, little, out var pointer))
        {
            var exifIfd = U32(tiff, pointer + 8, little);
            taken = ReadDate(tiff, exifIfd, TagDateTimeOriginal, little)
                    ?? ReadDate(tiff, exifIfd, TagDateTimeDigitized, little);
        }
        return taken ?? ReadDate(tiff, ifd0, TagDateTime, little);
    }

    // Walks JPEG marker segments up to the image data, returning the TIFF payload of the "Exif\0\0" APP1.
    private static ReadOnlySpan<byte> FindTiff(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return default;

        var pos = 2;
        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos] != 0xFF)
                return default;
            var marker = jpeg[pos + 1];
            if (marker == 0xFF) { pos++; continue; } // fill byte
            if (marker is 0xDA or 0xD9)              // start of scan / end of image: no EXIF
                return default;

            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(pos + 2)..]);
            if (length < 2)
                return default;
            var body = pos + 4;
            var end = Math.Min(pos + 2 + length, jpeg.Length);
            if (marker == 0xE1 && end - body > 6 && jpeg[body..(body + 6)].SequenceEqual("Exif\0\0"u8))
                return jpeg[(body + 6)..end];
            pos += 2 + length;
        }
        return default;
    }

    private static DateTime? ReadDate(ReadOnlySpan<byte> tiff, uint ifd, ushort tag, bool little)
    {
        if (!TryFindEntry(tiff, ifd, tag, little, out var entry))
            return null;
        // ASCII "YYYY:MM:DD HH:MM:SS\0" is 20 bytes, too long to be inline, so the value field is an offset.
        if (U16(tiff, entry + 2, little) != 2 || U32(tiff, entry + 4, little) < 19)
            return null;
        var offset = U32(tiff, entry + 8, little);
        if ((long)offset + 19 > tiff.Length)
            return null;

        var text = System.Text.Encoding.ASCII.GetString(tiff.Slice((int)offset, 19));
        return DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null; // "0000:00:00 00:00:00" and other placeholders
    }

    private static bool TryFindEntry(ReadOnlySpan<byte> tiff, uint ifd, ushort tag, bool little, out int entry)
    {
        entry = 0;
        if ((long)ifd + 2 > tiff.Length)
            return false;
        int count = U16(tiff, (int)ifd, little);
        for (var i = 0; i < count; i++)
        {
            var e = (int)ifd + 2 + i * 12;
            if (e + 12 > tiff.Length)
                return false;
            if (U16(tiff, e, little) == tag)
            {
                entry = e;
                return true;
            }
        }
        return false;
    }

    private static ushort U16(ReadOnlySpan<byte> s, int at, bool little)
        => little ? BinaryPrimitives.ReadUInt16LittleEndian(s[at..]) : BinaryPrimitives.ReadUInt16BigEndian(s[at..]);

    private static uint U32(ReadOnlySpan<byte> s, int at, bool little)
        => little ? BinaryPrimitives.ReadUInt32LittleEndian(s[at..]) : BinaryPrimitives.ReadUInt32BigEndian(s[at..]);
}
