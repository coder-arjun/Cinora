using Cinora.Application.Common.Tmdb;

namespace Cinora.Application.Common.Interfaces;

/// <summary>
/// The port that turns a raw TMDB image path (for example <c>/abc.jpg</c>) plus a requested
/// <see cref="TmdbImageSize"/> into an absolute image-CDN URL. Defined in Application so views and view
/// models depend only on the abstraction; the implementation composes the URL from the configured TMDB
/// image base URL (never hardcoded) in Infrastructure.
/// </summary>
public interface ITmdbImageUrlBuilder
{
    /// <summary>
    /// Builds an absolute TMDB image URL for the given raw path and size, or returns <c>null</c> when the
    /// path is <c>null</c> or blank (so callers can render a placeholder).
    /// </summary>
    /// <param name="path">The raw TMDB image path, with or without a leading slash.</param>
    /// <param name="size">The render size to request from the TMDB CDN.</param>
    /// <returns>The absolute image URL, or <c>null</c> when <paramref name="path"/> is <c>null</c> or blank.</returns>
    string? Build(string? path, TmdbImageSize size);
}
