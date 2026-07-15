using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Tmdb;
using Cinora.Application.Features.Discovery;
using Cinora.Domain.Enums;

namespace Cinora.Web.IntegrationTests.Infrastructure;

/// <summary>
/// A configurable in-memory <see cref="ITmdbClient"/> for integration tests. It replaces the real
/// HTTP-backed client via <c>ConfigureTestServices</c> so no live TMDB call is made. Genres and details
/// return canned data (Milestone 2.2); the trending/popular/top-rated rails return per-media canned lists
/// (Milestone 2.3), each defaulting to empty; search returns canned pages keyed by page number
/// (Milestone 2.4), or throws when <see cref="ThrowOnSearch"/> is set to model a post-resilience TMDB outage.
/// </summary>
internal sealed class FakeTmdbClient : ITmdbClient
{
    /// <summary>The canned movie genre list returned by <see cref="GetGenresAsync"/> for movies.</summary>
    public IReadOnlyList<TmdbGenre> MovieGenres { get; init; } = [];

    /// <summary>The canned series genre list returned by <see cref="GetGenresAsync"/> for series.</summary>
    public IReadOnlyList<TmdbGenre> SeriesGenres { get; init; } = [];

    /// <summary>The canned trending MOVIE rail returned by <see cref="GetTrendingAsync"/>.</summary>
    public IReadOnlyList<TmdbTitleSummary> TrendingMovies { get; init; } = [];

    /// <summary>The canned popular MOVIE rail returned by <see cref="GetPopularAsync"/>.</summary>
    public IReadOnlyList<TmdbTitleSummary> PopularMovies { get; init; } = [];

    /// <summary>The canned top-rated MOVIE rail returned by <see cref="GetTopRatedAsync"/>.</summary>
    public IReadOnlyList<TmdbTitleSummary> TopRatedMovies { get; init; } = [];

    /// <summary>The canned trending SERIES rail returned by <see cref="GetTrendingAsync"/>.</summary>
    public IReadOnlyList<TmdbTitleSummary> TrendingSeries { get; init; } = [];

    /// <summary>The canned popular SERIES rail returned by <see cref="GetPopularAsync"/>.</summary>
    public IReadOnlyList<TmdbTitleSummary> PopularSeries { get; init; } = [];

    /// <summary>The canned top-rated SERIES rail returned by <see cref="GetTopRatedAsync"/>.</summary>
    public IReadOnlyList<TmdbTitleSummary> TopRatedSeries { get; init; } = [];

    /// <summary>
    /// When <see langword="true"/>, the three rail methods (<see cref="GetTrendingAsync"/>,
    /// <see cref="GetPopularAsync"/>, <see cref="GetTopRatedAsync"/>) throw an
    /// <see cref="HttpRequestException"/> to model a TMDB outage that survives the real client's resilience
    /// pipeline. Defaults <see langword="false"/> so the canned lists above drive every other test unchanged.
    /// </summary>
    public bool ThrowOnRails { get; init; }

    /// <summary>
    /// The canned search result pages keyed by 1-based page number, returned by <see cref="SearchAsync"/>.
    /// A requested page that is absent yields an empty item list (a search miss), never a throw.
    /// </summary>
    public Dictionary<int, IReadOnlyList<TmdbTitleSummary>> SearchPages { get; } = [];

    /// <summary>The <c>TotalPages</c> reported on every <see cref="SearchAsync"/> page envelope.</summary>
    public int SearchTotalPages { get; init; }

    /// <summary>The <c>TotalResults</c> reported on every <see cref="SearchAsync"/> page envelope.</summary>
    public int SearchTotalResults { get; init; }

    /// <summary>
    /// When <see langword="true"/>, <see cref="SearchAsync"/> throws an <see cref="HttpRequestException"/> to
    /// model a TMDB outage that survives the real client's resilience pipeline, so the controller's
    /// graceful-degradation catch (→ <c>_SearchError</c>, HTTP 200) is exercised end-to-end.
    /// </summary>
    public bool ThrowOnSearch { get; init; }

    /// <summary>
    /// Canned title details keyed by (media type, TMDB id). A key that is absent models a TMDB 404 —
    /// <see cref="GetDetailsAsync"/> returns <c>null</c>.
    /// </summary>
    public Dictionary<(MediaType Media, int TmdbId), TmdbTitleDetails> Details { get; } = [];

    /// <summary>
    /// Canned "more like this" recommendations keyed by (media type, seed TMDB id), returned by
    /// <see cref="GetRecommendationsAsync"/> (Milestone 5.1). An absent key yields an empty list.
    /// </summary>
    public Dictionary<(MediaType Media, int TmdbId), IReadOnlyList<TmdbTitleSummary>> Recommendations { get; } = [];

    /// <summary>
    /// Canned discover-by-genre results keyed by media type, returned by <see cref="DiscoverByGenreAsync"/>
    /// (Milestone 5.1) whenever the requested genre id set is non-empty. An absent key yields an empty list.
    /// </summary>
    public Dictionary<MediaType, IReadOnlyList<TmdbTitleSummary>> DiscoverByGenre { get; } = [];

    /// <summary>
    /// Canned regional rails keyed by lower-case original-language code, returned by
    /// <see cref="GetRegionalRailAsync"/> (the Discover language filter). An absent code yields an empty list;
    /// distinct data per code lets a test prove the regional path was hit.
    /// </summary>
    public Dictionary<string, IReadOnlyList<TmdbTitleSummary>> RegionalRails { get; } = [];

    public Task<IReadOnlyList<TmdbGenre>> GetGenresAsync(MediaType media, CancellationToken cancellationToken) =>
        Task.FromResult(media == MediaType.Movie ? MovieGenres : SeriesGenres);

    public Task<TmdbTitleDetails?> GetDetailsAsync(MediaType media, int tmdbId, CancellationToken cancellationToken) =>
        Task.FromResult(Details.TryGetValue((media, tmdbId), out var details) ? details : null);

    public Task<IReadOnlyList<TmdbTitleSummary>> GetTrendingAsync(MediaType media, CancellationToken cancellationToken) =>
        ThrowOnRails
            ? throw RailOutage()
            : Task.FromResult(media == MediaType.Movie ? TrendingMovies : TrendingSeries);

    public Task<IReadOnlyList<TmdbTitleSummary>> GetPopularAsync(MediaType media, CancellationToken cancellationToken) =>
        ThrowOnRails
            ? throw RailOutage()
            : Task.FromResult(media == MediaType.Movie ? PopularMovies : PopularSeries);

    public Task<IReadOnlyList<TmdbTitleSummary>> GetTopRatedAsync(MediaType media, CancellationToken cancellationToken) =>
        ThrowOnRails
            ? throw RailOutage()
            : Task.FromResult(media == MediaType.Movie ? TopRatedMovies : TopRatedSeries);

    // Models the exception the real HTTP client surfaces after its resilience pipeline gives up, so the
    // controller's graceful-degradation catch (→ _RailError, HTTP 200) is exercised end-to-end.
    private static HttpRequestException RailOutage() =>
        new("Simulated TMDB rail outage (post-resilience) for the error-state regression test.");

    public Task<TmdbPage<TmdbTitleSummary>> SearchAsync(
        MediaType media,
        string query,
        int page,
        CancellationToken cancellationToken) =>
        ThrowOnSearch
            ? throw SearchOutage()
            : Task.FromResult(new TmdbPage<TmdbTitleSummary>
            {
                Page = page,
                TotalPages = SearchTotalPages,
                TotalResults = SearchTotalResults,
                Items = SearchPages.GetValueOrDefault(page, []),
            });

    // Models the exception the real HTTP client surfaces after its resilience pipeline gives up, so the
    // controller's graceful-degradation catch (→ _SearchError, HTTP 200) is exercised end-to-end.
    private static HttpRequestException SearchOutage() =>
        new("Simulated TMDB search outage (post-resilience) for the error-state regression test.");

    public Task<IReadOnlyList<TmdbTitleSummary>> GetRecommendationsAsync(MediaType media, int tmdbId, CancellationToken cancellationToken) =>
        Task.FromResult(Recommendations.GetValueOrDefault((media, tmdbId), []));

    public Task<IReadOnlyList<TmdbTitleSummary>> DiscoverByGenreAsync(MediaType media, IReadOnlyList<int> genreTmdbIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TmdbTitleSummary>>(
            genreTmdbIds.Count == 0 ? [] : DiscoverByGenre.GetValueOrDefault(media, []));

    public Task<IReadOnlyList<TmdbTitleSummary>> GetRegionalRailAsync(MediaType media, RailKind kind, string originalLanguage, CancellationToken cancellationToken) =>
        ThrowOnRails
            ? throw RailOutage()
            : Task.FromResult(RegionalRails.GetValueOrDefault(originalLanguage.ToLowerInvariant(), []));
}
