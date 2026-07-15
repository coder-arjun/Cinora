namespace Cinora.Application.Common.Tmdb;

/// <summary>
/// The TMDB image render size a caller wants when building a poster, backdrop, or profile URL. Sizes are
/// a presentation concern (a list must never request <see cref="Original"/>), so the view chooses one and
/// <see cref="Interfaces.ITmdbImageUrlBuilder"/> maps it to the corresponding TMDB CDN size token.
/// </summary>
public enum TmdbImageSize
{
    /// <summary>TMDB <c>w185</c> — cast thumbnails and small profile images.</summary>
    W185,

    /// <summary>TMDB <c>w342</c> — rail and search result poster cards.</summary>
    W342,

    /// <summary>TMDB <c>w500</c> — the detail-page poster.</summary>
    W500,

    /// <summary>TMDB <c>w780</c> — large posters and small backdrops.</summary>
    W780,

    /// <summary>TMDB <c>w1280</c> — the detail-page backdrop hero.</summary>
    W1280,

    /// <summary>TMDB <c>original</c> — the full-resolution source image (avoid in lists).</summary>
    Original,
}
