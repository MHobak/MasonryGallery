namespace MasonryGallery.Application.Pipeline;

/// <summary>
/// Encoding of Media.Placeholder: the thumbnail's average colour as 3 bytes (R, G, B).
/// Tiles paint it until the thumbnail loads, so the grid is never blank (design §6).
/// </summary>
public static class PlaceholderColor
{
    public const int None = -1;

    public static byte[] ToBytes(int rgb) => [(byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb];

    /// <returns>0xRRGGBB, or <see cref="None"/> when no colour is stored.</returns>
    public static int FromBytes(byte[]? bytes)
        => bytes is { Length: 3 } ? (bytes[0] << 16) | (bytes[1] << 8) | bytes[2] : None;
}
