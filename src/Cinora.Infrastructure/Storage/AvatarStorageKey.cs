using System.Text.RegularExpressions;

namespace Cinora.Infrastructure.Storage;

/// <summary>
/// The single authority on avatar storage-key shape (ADR 0013 §2.2). A key is <b>server-generated</b> as
/// <c>avatars/{32-hex}{ext}</c> where the extension comes from the magic-byte-confirmed type — never the client
/// filename or declared content-type — so there is no user-controlled path component and path traversal is
/// impossible by construction. Read/delete/serve paths additionally re-validate any key against
/// <see cref="KeyPattern"/> as defense-in-depth. Also maps the confirmed format to its extension and the
/// stored extension back to the forced serving content-type.
/// </summary>
internal static partial class AvatarStorageKey
{
    /// <summary>
    /// The strict key grammar: <c>avatars/</c> + 32 lowercase hex + one of the three allowed extensions. The end
    /// anchor is <c>\z</c> (strict end-of-string), NOT <c>$</c>: in non-Multiline .NET regex <c>$</c> also matches
    /// immediately before a single trailing <c>\n</c>, so a key like <c>avatars/…jpg\n</c> would slip through this
    /// single key authority.
    /// </summary>
    [GeneratedRegex("^avatars/[0-9a-f]{32}\\.(jpg|png|webp)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    /// <summary>
    /// Mints a fresh, server-owned key for a confirmed image format. The random 32-hex component makes the key
    /// unguessable and collision-free; the extension is derived from <paramref name="format"/> (the confirmed
    /// type), not from any client input.
    /// </summary>
    /// <param name="format">The magic-byte-confirmed image format.</param>
    /// <returns>A new key of the form <c>avatars/{32-hex}{ext}</c>.</returns>
    public static string New(ImageFormat format) => $"avatars/{Guid.NewGuid():N}{Extension(format)}";

    /// <summary>Whether <paramref name="key"/> matches the strict avatar-key grammar.</summary>
    /// <param name="key">The candidate storage key.</param>
    /// <returns><see langword="true"/> for a well-formed avatar key; otherwise <see langword="false"/>.</returns>
    public static bool IsValid(string? key) => !string.IsNullOrEmpty(key) && KeyPattern().IsMatch(key);

    /// <summary>The file extension (including the dot) for a confirmed format.</summary>
    /// <param name="format">The confirmed image format.</param>
    /// <returns><c>.jpg</c>, <c>.png</c>, or <c>.webp</c>.</returns>
    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Png => ".png",
        ImageFormat.Webp => ".webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported image format."),
    };

    /// <summary>
    /// The content-type to force when serving a stored key. Because the extension was set from the confirmed
    /// magic-byte type at save time, this is the file's TRUE type — served with <c>nosniff</c> so a crafted
    /// polyglot cannot execute (ADR 0013 §2.4). The key is assumed valid (<see cref="IsValid"/>).
    /// </summary>
    /// <param name="key">A valid avatar storage key.</param>
    /// <returns>The forced <c>image/*</c> content-type.</returns>
    public static string ContentTypeForKey(string key)
    {
        var extension = Path.GetExtension(key.AsSpan());
        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase))
        {
            return "image/jpeg";
        }

        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return "image/png";
        }

        if (extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            return "image/webp";
        }

        throw new ArgumentException($"Storage key '{key}' has an unsupported extension.", nameof(key));
    }
}
