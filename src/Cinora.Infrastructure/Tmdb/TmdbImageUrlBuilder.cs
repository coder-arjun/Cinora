using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Tmdb;
using Cinora.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Cinora.Infrastructure.Tmdb;

/// <summary>
/// Builds poster/backdrop image URLs from a raw TMDB path and a <see cref="TmdbImageSize"/>, routing them
/// through the free <c>images.weserv.nl</c> image proxy/CDN. The upstream TMDB URL is derived from the
/// configured <see cref="TmdbOptions.ImageBaseUrl"/> (never hardcoded). Stateless and registered as a
/// singleton. Internal — callers depend on the <see cref="ITmdbImageUrlBuilder"/> port.
/// </summary>
internal sealed class TmdbImageUrlBuilder : ITmdbImageUrlBuilder
{
    private readonly string _imageBaseUrl;

    /// <summary>Creates the builder, capturing the configured image base URL (trailing slash trimmed).</summary>
    /// <param name="options">The bound TMDB options supplying <see cref="TmdbOptions.ImageBaseUrl"/>.</param>
    public TmdbImageUrlBuilder(IOptions<TmdbOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _imageBaseUrl = options.Value.ImageBaseUrl.TrimEnd('/');
    }

    /// <inheritdoc />
    public string? Build(string? path, TmdbImageSize size)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // TMDB paths normally include a leading slash; tolerate its absence so callers cannot produce a
        // malformed "…/w342abc.jpg" URL.
        var normalizedPath = path.StartsWith('/') ? path : "/" + path;
        var tmdbUrl = $"{_imageBaseUrl}/{ToSizeToken(size)}{normalizedPath}";

        // Route the poster through the free weserv.nl image proxy/CDN instead of hitting image.tmdb.org
        // directly. TMDB's image CDN is unreachable from the deployed host AND blocked on some client
        // networks/regions, so a direct <img> load fails there; weserv fetches it (its network reaches TMDB)
        // and serves it from a globally-reachable, cached CDN. The upstream is passed without its scheme,
        // which is weserv's expected `url=` form.
        var upstream = tmdbUrl.Replace("https://", string.Empty, StringComparison.Ordinal);
        return $"https://images.weserv.nl/?url={upstream}";
    }

    private static string ToSizeToken(TmdbImageSize size) => size switch
    {
        TmdbImageSize.W185 => "w185",
        TmdbImageSize.W342 => "w342",
        TmdbImageSize.W500 => "w500",
        TmdbImageSize.W780 => "w780",
        TmdbImageSize.W1280 => "w1280",
        TmdbImageSize.Original => "original",
        _ => "w342",
    };
}
