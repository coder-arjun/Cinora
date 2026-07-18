using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Tmdb;
using Cinora.Application.Features.Discovery;
using Cinora.Domain.Enums;

namespace Cinora.Application.Tests.Common.Fakes;

/// <summary>
/// A minimal <see cref="ITmdbClient"/> stub for Application handler unit tests. Only the two reads the
/// catalog race handlers make are configurable — <see cref="Details"/> (for EnsureTitleCached) and the
/// movie/series genre lists (for SyncGenres); every other member throws, so an unexpected call is a loud
/// test-wiring bug rather than a silent default. No network, no TMDB key.
/// </summary>
internal sealed class StubTmdbClient : ITmdbClient
{
    /// <summary>The canned details returned by <see cref="GetDetailsAsync"/> (<c>null</c> models a 404).</summary>
    public TmdbTitleDetails? Details { get; set; }

    /// <summary>The canned movie genre list returned by <see cref="GetGenresAsync"/> for movies.</summary>
    public IReadOnlyList<TmdbGenre> MovieGenres { get; set; } = [];

    /// <summary>The canned series genre list returned by <see cref="GetGenresAsync"/> for series.</summary>
    public IReadOnlyList<TmdbGenre> SeriesGenres { get; set; } = [];

    /// <summary>
    /// The canned "more like this" recommendations keyed by (media, seed TMDB id). Left <c>null</c> unless a
    /// test sets it, so an unexpected <see cref="GetRecommendationsAsync"/> call is a loud test-wiring bug.
    /// </summary>
    public IReadOnlyDictionary<(MediaType Media, int TmdbId), IReadOnlyList<TmdbTitleSummary>>? Recommendations { get; set; }

    /// <summary>
    /// The canned discover-by-genre results keyed by media. Left <c>null</c> unless a test sets it, so an
    /// unexpected <see cref="DiscoverByGenreAsync"/> call is a loud test-wiring bug.
    /// </summary>
    public IReadOnlyDictionary<MediaType, IReadOnlyList<TmdbTitleSummary>>? Discover { get; set; }

    /// <inheritdoc />
    public Task<TmdbTitleDetails?> GetDetailsAsync(MediaType media, int tmdbId, CancellationToken cancellationToken) =>
        Task.FromResult(Details);

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbGenre>> GetGenresAsync(MediaType media, CancellationToken cancellationToken) =>
        Task.FromResult(media == MediaType.Series ? SeriesGenres : MovieGenres);

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbTitleSummary>> GetTrendingAsync(MediaType media, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbTitleSummary>> GetPopularAsync(MediaType media, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbTitleSummary>> GetTopRatedAsync(MediaType media, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public Task<TmdbPage<TmdbTitleSummary>> SearchAsync(MediaType media, string query, int page, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public Task<TmdbPage<TmdbTitleSummary>> SearchMultiAsync(string query, int page, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbTitleSummary>> GetRecommendationsAsync(MediaType media, int tmdbId, CancellationToken cancellationToken) =>
        Recommendations is null
            ? throw new NotSupportedException()
            : Task.FromResult(Recommendations.GetValueOrDefault((media, tmdbId), []));

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbTitleSummary>> DiscoverByGenreAsync(MediaType media, IReadOnlyList<int> genreTmdbIds, CancellationToken cancellationToken) =>
        Discover is null
            ? throw new NotSupportedException()
            : Task.FromResult<IReadOnlyList<TmdbTitleSummary>>(
                genreTmdbIds.Count == 0 ? [] : Discover.GetValueOrDefault(media, []));

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbTitleSummary>> GetRegionalRailAsync(MediaType media, RailKind kind, string originalLanguage, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public Task<TmdbPersonCredits?> GetPersonCreditsAsync(int personId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
