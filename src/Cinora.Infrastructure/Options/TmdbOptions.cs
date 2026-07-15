using System.ComponentModel.DataAnnotations;

namespace Cinora.Infrastructure.Options;

/// <summary>
/// Binds the <c>Tmdb</c> configuration section describing the free TMDB (The Movie Database) API used
/// for movie and series metadata. The <see cref="ApiKey"/> is a secret supplied via user-secrets in
/// development and environment variables in production — never committed to <c>appsettings*.json</c>,
/// which carries only the non-secret <see cref="BaseUrl"/> and <see cref="ImageBaseUrl"/> defaults.
/// Bound with <c>ValidateDataAnnotations</c> (lazy) but deliberately NOT <c>ValidateOnStart</c> in
/// Phase 1 — nothing consumes TMDB yet, so a missing key must not fail application boot
/// (solution-structure.md §6). The typed <c>ITmdbClient</c> that consumes these values arrives in Phase 2.
/// </summary>
public sealed class TmdbOptions
{
    /// <summary>The configuration section name bound to this options type.</summary>
    public const string SectionName = "Tmdb";

    /// <summary>
    /// The TMDB API key (free, non-commercial tier). Supplied via user-secrets / environment variables,
    /// never <c>appsettings*.json</c>. Required once TMDB is actually consumed (Phase 2).
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The TMDB REST API base URL.</summary>
    [Required(AllowEmptyStrings = false)]
    [Url]
    public string BaseUrl { get; set; } = "https://api.themoviedb.org/3";

    /// <summary>The TMDB image CDN base URL, used to build poster and backdrop URLs from image paths.</summary>
    [Required(AllowEmptyStrings = false)]
    [Url]
    public string ImageBaseUrl { get; set; } = "https://image.tmdb.org/t/p";
}
