using System.Text;

namespace Cinora.Infrastructure.Tests.Storage;

/// <summary>
/// Builds byte payloads with correct (or deliberately wrong) leading magic bytes for the upload-gate tests:
/// valid JPEG/PNG/WebP headers, plus an SVG and a plain-text body that must be rejected.
/// </summary>
internal static class AvatarImageFixtures
{
    /// <summary>A payload with a valid JPEG signature (<c>FF D8 FF E0</c>), zero-padded to <paramref name="length"/>.</summary>
    public static byte[] Jpeg(int length = 64)
    {
        var bytes = new byte[Math.Max(length, 4)];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;
        return bytes;
    }

    /// <summary>A payload with the valid 8-byte PNG signature, zero-padded to <paramref name="length"/>.</summary>
    public static byte[] Png(int length = 64)
    {
        var bytes = new byte[Math.Max(length, 8)];
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);
        return bytes;
    }

    /// <summary>A payload with a valid <c>RIFF….WEBP</c> container signature, zero-padded to <paramref name="length"/>.</summary>
    public static byte[] Webp(int length = 64)
    {
        var bytes = new byte[Math.Max(length, 12)];
        bytes[0] = (byte)'R';
        bytes[1] = (byte)'I';
        bytes[2] = (byte)'F';
        bytes[3] = (byte)'F';
        bytes[8] = (byte)'W';
        bytes[9] = (byte)'E';
        bytes[10] = (byte)'B';
        bytes[11] = (byte)'P';
        return bytes;
    }

    /// <summary>An SVG body — the classic image-as-XSS vector that must be rejected (never in the allow-list).</summary>
    public static byte[] Svg() => Encoding.UTF8.GetBytes(
        "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");

    /// <summary>A plain-text body that is not any image type.</summary>
    public static byte[] Text() => Encoding.UTF8.GetBytes("this is definitely not an image file");
}
