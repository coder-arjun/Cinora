namespace Cinora.Infrastructure.Storage;

/// <summary>
/// The image format confirmed by <see cref="ImageContentInspector"/> from a file's leading magic bytes. Only
/// the three web image types Cinora accepts are representable — everything else (SVG, GIF, HTML, arbitrary
/// binary) is undetectable and therefore rejected at the upload gate (ADR 0013 §2.3).
/// </summary>
internal enum ImageFormat
{
    /// <summary>JPEG — magic bytes <c>FF D8 FF</c>.</summary>
    Jpeg,

    /// <summary>PNG — magic bytes <c>89 50 4E 47 0D 0A 1A 0A</c>.</summary>
    Png,

    /// <summary>WebP — <c>RIFF</c>....<c>WEBP</c> container signature.</summary>
    Webp,
}

/// <summary>
/// Derives an upload's <b>true</b> image type from its leading magic bytes. Modeled as a port (like
/// <c>ITmdbImageUrlBuilder</c>) so the storage adapter depends on the abstraction and it is independently
/// registered/testable.
/// </summary>
internal interface IImageContentInspector
{
    /// <summary>
    /// Attempts to identify the image format from the supplied leading bytes.
    /// </summary>
    /// <param name="header">The file's leading bytes (up to <see cref="ImageContentInspector.RequiredHeaderBytes"/>).</param>
    /// <param name="format">The detected format when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the bytes match JPEG, PNG, or WebP; otherwise <see langword="false"/>.</returns>
    bool TryDetect(ReadOnlySpan<byte> header, out ImageFormat format);
}

/// <summary>
/// Dependency-free magic-byte sniffer that derives an upload's <b>true</b> image type from its leading bytes,
/// so the declared content-type (attacker-controlled) is never trusted. This is Cinora's free defense against
/// content-type spoofing and image-as-XSS (SVG/polyglots) in place of a decode/re-encode library — whose
/// license is the same posture that banned MediatR/FluentAssertions (ADR 0013). A file whose bytes are not one
/// of JPEG/PNG/WebP is undetectable here and rejected by <see cref="LocalFileStorage.SaveAsync"/>.
/// </summary>
internal sealed class ImageContentInspector : IImageContentInspector
{
    /// <summary>
    /// The number of leading bytes required to positively identify any supported format. WebP needs the most
    /// (the 4-byte <c>RIFF</c> tag, a 4-byte size, then the 4-byte <c>WEBP</c> form-type at offset 8).
    /// </summary>
    public const int RequiredHeaderBytes = 12;

    /// <inheritdoc />
    public bool TryDetect(ReadOnlySpan<byte> header, out ImageFormat format)
    {
        // JPEG: FF D8 FF (SOI + start of the first marker).
        if (header.Length >= 3
            && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            format = ImageFormat.Jpeg;
            return true;
        }

        // PNG: the fixed 8-byte signature 89 50 4E 47 0D 0A 1A 0A.
        if (header.Length >= 8
            && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
            && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            format = ImageFormat.Png;
            return true;
        }

        // WebP: a RIFF container ("RIFF" .... "WEBP") — bytes 0-3 == RIFF and bytes 8-11 == WEBP.
        if (header.Length >= 12
            && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
            && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
        {
            format = ImageFormat.Webp;
            return true;
        }

        format = default;
        return false;
    }
}
