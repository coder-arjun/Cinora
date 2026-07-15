namespace Cinora.Application.Features.Recommendations;

/// <summary>
/// Shared text helpers for the recommendation pipeline. Currently a single surrogate-safe truncation reused by
/// the generation guard (capping the model's "why this" reason) and the deterministic heuristic (capping its
/// template reasons), so both cap identically and neither can split a UTF-16 surrogate pair.
/// </summary>
internal static class RecommendationText
{
    /// <summary>
    /// Trims <paramref name="value"/> and truncates it to at most <paramref name="maxLength"/> characters
    /// WITHOUT splitting a UTF-16 surrogate pair (a dangling high surrogate is dropped), then trims trailing
    /// whitespace left by the cut. Returns <see cref="string.Empty"/> for a null/empty input.
    /// </summary>
    /// <param name="value">The text to truncate.</param>
    /// <param name="maxLength">The maximum number of characters to keep.</param>
    /// <returns>The trimmed, surrogate-safe, length-capped text.</returns>
    public static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        var end = maxLength;
        if (char.IsHighSurrogate(trimmed[end - 1]))
        {
            end--; // the last kept char is a lone high surrogate; drop it so no pair is split
        }

        return trimmed[..end].TrimEnd();
    }
}
