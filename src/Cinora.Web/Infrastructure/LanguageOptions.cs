namespace Cinora.Web.Infrastructure;

/// <summary>
/// The curated set of movie languages offered in the Discover language dropdown and the profile default-language
/// setting. "Global" is the empty code (<see cref="GlobalCode"/>) meaning no original-language filter — the
/// trending/popular/top-rated behaviour; every other entry is a lower-case ISO-639-1 code TMDB's
/// <c>with_original_language</c> filter accepts. Central so the /discover dropdown, the /settings selector, and
/// their server-side validation all share ONE list (no drift).
/// </summary>
public static class LanguageOptions
{
    /// <summary>The empty code that means "Global" — no original-language filter.</summary>
    public const string GlobalCode = "";

    /// <summary>The ordered languages: Global first, then a curated regional + world set.</summary>
    public static IReadOnlyList<LanguageOption> All { get; } =
    [
        new(GlobalCode, "Global"),
        new("en", "English"),
        new("hi", "Hindi"),
        new("ta", "Tamil"),
        new("te", "Telugu"),
        new("ml", "Malayalam"),
        new("kn", "Kannada"),
        new("ko", "Korean"),
        new("ja", "Japanese"),
        new("es", "Spanish"),
        new("fr", "French"),
    ];

    // The non-empty supported codes, case-insensitive, for validation/normalization.
    private static readonly HashSet<string> SupportedCodes =
        new(All.Where(o => o.Code.Length > 0).Select(o => o.Code), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the supplied code is supported. Null / empty / whitespace is Global (supported — it means "no
    /// filter"); a non-empty code is supported only if it is in the curated list (case-insensitive).
    /// </summary>
    /// <param name="code">The raw code to check.</param>
    /// <returns><c>true</c> when the code is Global or a curated language.</returns>
    public static bool IsSupported(string? code) =>
        string.IsNullOrWhiteSpace(code) || SupportedCodes.Contains(code.Trim());

    /// <summary>
    /// Normalizes a raw code to the canonical stored form: a supported non-empty code lower-cased, otherwise
    /// <c>null</c> (Global). Unsupported input collapses to Global rather than erroring.
    /// </summary>
    /// <param name="code">The raw code (from a query string, form field, or stored value).</param>
    /// <returns>The lower-case ISO-639-1 code, or <c>null</c> for Global.</returns>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim();
        return SupportedCodes.Contains(trimmed) ? trimmed.ToLowerInvariant() : null;
    }
}

/// <summary>One selectable language: its ISO-639-1 code (empty = Global) and its English display name.</summary>
/// <param name="Code">The lower-case ISO-639-1 code, or empty for Global.</param>
/// <param name="Name">The English display name shown in the dropdown.</param>
public sealed record LanguageOption(string Code, string Name);
