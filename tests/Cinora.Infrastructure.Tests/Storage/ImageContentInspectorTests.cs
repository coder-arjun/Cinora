using Cinora.Infrastructure.Storage;

namespace Cinora.Infrastructure.Tests.Storage;

/// <summary>
/// Verifies the magic-byte sniffer identifies the three allowed image types from their leading bytes and
/// rejects everything else (SVG, plain text, empty, or truncated headers) — the free defense against
/// content-type spoofing (ADR 0013 §2.3).
/// </summary>
public sealed class ImageContentInspectorTests
{
    private readonly ImageContentInspector _inspector = new();

    [Fact]
    public void Detects_jpeg_from_its_signature()
    {
        Assert.True(_inspector.TryDetect(AvatarImageFixtures.Jpeg(), out var format));
        Assert.Equal(ImageFormat.Jpeg, format);
    }

    [Fact]
    public void Detects_png_from_its_signature()
    {
        Assert.True(_inspector.TryDetect(AvatarImageFixtures.Png(), out var format));
        Assert.Equal(ImageFormat.Png, format);
    }

    [Fact]
    public void Detects_webp_from_its_riff_container()
    {
        Assert.True(_inspector.TryDetect(AvatarImageFixtures.Webp(), out var format));
        Assert.Equal(ImageFormat.Webp, format);
    }

    [Fact]
    public void Rejects_svg()
    {
        Assert.False(_inspector.TryDetect(AvatarImageFixtures.Svg(), out _));
    }

    [Fact]
    public void Rejects_plain_text()
    {
        Assert.False(_inspector.TryDetect(AvatarImageFixtures.Text(), out _));
    }

    [Fact]
    public void Rejects_an_empty_header()
    {
        Assert.False(_inspector.TryDetect(ReadOnlySpan<byte>.Empty, out _));
    }

    [Fact]
    public void Rejects_a_riff_that_is_not_webp()
    {
        // A RIFF container that is not a WEBP form-type (e.g. WAV/AVI) must not pass as an image.
        byte[] riffWave = [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'A', (byte)'V', (byte)'E'];
        Assert.False(_inspector.TryDetect(riffWave, out _));
    }

    [Fact]
    public void Rejects_a_truncated_jpeg_header()
    {
        // Only 2 bytes (FF D8), one short of JPEG's 3-byte FF D8 FF signature — the 'header.Length >= 3' guard
        // must reject rather than read past the span.
        byte[] truncatedJpeg = [0xFF, 0xD8];
        Assert.False(_inspector.TryDetect(truncatedJpeg, out _));
    }

    [Fact]
    public void Rejects_a_truncated_webp_header()
    {
        // 11 bytes, one short of WebP's 12-byte RIFF....WEBP signature (missing the final 'P') — the
        // 'header.Length >= 12' guard must reject rather than read past the span.
        byte[] truncatedWebp = [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B'];
        Assert.False(_inspector.TryDetect(truncatedWebp, out _));
    }
}
