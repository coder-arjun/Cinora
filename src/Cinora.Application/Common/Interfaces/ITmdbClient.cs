using Cinora.Application.Common.Tmdb;
using Cinora.Application.Features.Discovery;
using Cinora.Domain.Enums;

namespace Cinora.Application.Common.Interfaces;

/// <summary>
/// The port for reading movie and series metadata from TMDB (trending / popular / top-rated rails, search,
/// and title details with credits, plus genre lists). Defined in Application so handlers depend only on the
/// abstraction and are testable with a fake — no <c>HttpClient</c>, no network (ADR 0006). The implementation
/// (a typed <c>HttpClient</c> with the standard resilience pipeline) lives in Infrastructure; a caching
/// decorator wraps it from Milestone 2.1. Every method returns Application read models — never TMDB DTOs and
/// never Domain entities — and threads a <see cref="CancellationToken"/> through to the underlying call.
/// </summary>
public interface ITmdbClient
{
    /// <summary>Gets the current weekly trending titles for a media type.</summary>
    /// <param name="media">Whether to fetch trending movies or series.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The trending title summaries (first page).</returns>
    Task<IReadOnlyList<TmdbTitleSummary>> GetTrendingAsync(MediaType media, CancellationToken cancellationToken);

    /// <summary>Gets the currently popular titles for a media type.</summary>
    /// <param name="media">Whether to fetch popular movies or series.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The popular title summaries (first page).</returns>
    Task<IReadOnlyList<TmdbTitleSummary>> GetPopularAsync(MediaType media, CancellationToken cancellationToken);

    /// <summary>Gets the top-rated titles for a media type.</summary>
    /// <param name="media">Whether to fetch top-rated movies or series.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The top-rated title summaries (first page).</returns>
    Task<IReadOnlyList<TmdbTitleSummary>> GetTopRatedAsync(MediaType media, CancellationToken cancellationToken);

    /// <summary>Searches TMDB for titles of a media type matching a query.</summary>
    /// <param name="media">Whether to search movies or series.</param>
    /// <param name="query">The search text (the caller is expected to have validated it non-empty).</param>
    /// <param name="page">The 1-based page to fetch.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>One page of matching title summaries with paging metadata.</returns>
    Task<TmdbPage<TmdbTitleSummary>> SearchAsync(MediaType media, string query, int page, CancellationToken cancellationToken);

    /// <summary>
    /// Searches TMDB across ALL title kinds at once (movies AND series) via <c>search/multi</c> — the unified
    /// search behind one box. Each returned summary carries its own <see cref="TmdbTitleSummary.MediaType"/>;
    /// non-title results (people) are dropped.
    /// </summary>
    /// <param name="query">The search text (the caller is expected to have validated it non-empty).</param>
    /// <param name="page">The 1-based page to fetch.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>One page of matching movie + series summaries with paging metadata.</returns>
    Task<TmdbPage<TmdbTitleSummary>> SearchMultiAsync(string query, int page, CancellationToken cancellationToken);

    /// <summary>Gets the full details of a single title, including its genres and a ranked cast summary.</summary>
    /// <param name="media">Whether the title is a movie or a series.</param>
    /// <param name="tmdbId">The TMDB identifier of the title.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The title details, or <c>null</c> when TMDB has no such title (404).</returns>
    Task<TmdbTitleDetails?> GetDetailsAsync(MediaType media, int tmdbId, CancellationToken cancellationToken);

    /// <summary>Gets the full genre list for a media type (used to resolve genre ids to names).</summary>
    /// <param name="media">Whether to fetch the movie or the series genre list.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The genres for the media type.</returns>
    Task<IReadOnlyList<TmdbGenre>> GetGenresAsync(MediaType media, CancellationToken cancellationToken);

    /// <summary>
    /// Gets titles TMDB recommends for a given title — the "more like this" fan-out that grounds AI
    /// recommendation candidate generation in real, resolvable titles (ADR 0017). Returns the first page.
    /// </summary>
    /// <param name="media">Whether the seed title is a movie or a series (recommendations share its media type).</param>
    /// <param name="tmdbId">The TMDB identifier of the seed title to fetch recommendations for.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The recommended title summaries (first page); empty when TMDB has none.</returns>
    Task<IReadOnlyList<TmdbTitleSummary>> GetRecommendationsAsync(MediaType media, int tmdbId, CancellationToken cancellationToken);

    /// <summary>
    /// Discovers popular titles of a media type tagged with any of the supplied TMDB genre ids — the
    /// "in your favorite genres" fan-out that grounds AI recommendation candidate generation (ADR 0017),
    /// sorted by popularity. Returns the first page; an empty <paramref name="genreTmdbIds"/> yields an empty
    /// list without calling TMDB.
    /// </summary>
    /// <param name="media">Whether to discover movies or series.</param>
    /// <param name="genreTmdbIds">The TMDB genre ids to discover within; empty yields an empty result.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The discovered title summaries (first page), most popular first.</returns>
    Task<IReadOnlyList<TmdbTitleSummary>> DiscoverByGenreAsync(MediaType media, IReadOnlyList<int> genreTmdbIds, CancellationToken cancellationToken);

    /// <summary>
    /// Discovers titles ORIGINALLY in the given language (regional cinema) for a Home rail, via TMDB
    /// <c>discover</c> with <c>with_original_language</c> — the "show films from that language" path behind the
    /// Discover language dropdown. <paramref name="kind"/> selects the sort (and, for Trending, a recency
    /// window) so the three regional rails differ. "Global" (no filter) is served by the trending/popular/
    /// top-rated methods instead, so this is only called for a specific, supported language.
    /// </summary>
    /// <param name="media">Whether to discover movies or series.</param>
    /// <param name="kind">The rail whose sort/recency to apply (Trending / Popular / Top Rated).</param>
    /// <param name="originalLanguage">The lower-case ISO-639-1 original language to filter to (never empty).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The discovered title summaries (first page); empty when TMDB returned nothing.</returns>
    Task<IReadOnlyList<TmdbTitleSummary>> GetRegionalRailAsync(MediaType media, RailKind kind, string originalLanguage, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a person (actor) and their acting filmography (TMDB <c>person/{id}</c> with
    /// <c>combined_credits</c>) — the "click a cast member to see their titles" fan-out (feature #9).
    /// </summary>
    /// <param name="personId">The TMDB person identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The person with their acting credits, or <c>null</c> when TMDB has no such person (404).</returns>
    Task<TmdbPersonCredits?> GetPersonCreditsAsync(int personId, CancellationToken cancellationToken);
}
