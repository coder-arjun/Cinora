using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Tmdb;
using Cinora.Application.Features.Discovery;
using Cinora.Domain.Enums;

namespace Cinora.Infrastructure.Tests.Tmdb;

/// <summary>
/// A fake inner <see cref="ITmdbClient"/> for the <see cref="Cinora.Infrastructure.Tmdb.CachedTmdbClient"/>
/// tests. It records how many times each method is invoked — so a test can prove a cache hit skipped the
/// inner call — and returns configurable canned read models. No network, no TMDB key.
/// </summary>
internal sealed class FakeTmdbClient : ITmdbClient
{
    /// <summary>The number of times <see cref="GetTrendingAsync"/> was invoked.</summary>
    public int GetTrendingCount { get; private set; }

    /// <summary>The number of times <see cref="GetPopularAsync"/> was invoked.</summary>
    public int GetPopularCount { get; private set; }

    /// <summary>The number of times <see cref="GetTopRatedAsync"/> was invoked.</summary>
    public int GetTopRatedCount { get; private set; }

    /// <summary>The number of times <see cref="SearchAsync"/> was invoked.</summary>
    public int SearchCount { get; private set; }

    /// <summary>The number of times <see cref="GetDetailsAsync"/> was invoked.</summary>
    public int GetDetailsCount { get; private set; }

    /// <summary>The number of times <see cref="GetGenresAsync"/> was invoked.</summary>
    public int GetGenresCount { get; private set; }

    /// <summary>The number of times <see cref="GetRecommendationsAsync"/> was invoked.</summary>
    public int GetRecommendationsCount { get; private set; }

    /// <summary>The number of times <see cref="DiscoverByGenreAsync"/> was invoked.</summary>
    public int DiscoverCount { get; private set; }

    /// <summary>The number of times <see cref="GetRegionalRailAsync"/> was invoked.</summary>
    public int GetRegionalCount { get; private set; }

    /// <summary>The canned rail (trending/popular/top-rated) result.</summary>
    public IReadOnlyList<TmdbTitleSummary> RailResult { get; set; } = [Summary(27205, "Inception")];

    /// <summary>The canned genres result.</summary>
    public IReadOnlyList<TmdbGenre> GenresResult { get; set; } = [new TmdbGenre { TmdbGenreId = 28, Name = "Action" }];

    /// <summary>The canned search page result.</summary>
    public TmdbPage<TmdbTitleSummary> SearchResult { get; set; } = Page(27205, "Inception");

    /// <summary>The canned details result; set to <c>null</c> to model a 404.</summary>
    public TmdbTitleDetails? DetailsResult { get; set; } = Details(27205, "Inception");

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbTitleSummary>> GetTrendingAsync(MediaType media, CancellationToken cancellationToken)
    {
        GetTrendingCount++;
        return Task.FromResult(RailResult);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbTitleSummary>> GetPopularAsync(MediaType media, CancellationToken cancellationToken)
    {
        GetPopularCount++;
        return Task.FromResult(RailResult);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbTitleSummary>> GetTopRatedAsync(MediaType media, CancellationToken cancellationToken)
    {
        GetTopRatedCount++;
        return Task.FromResult(RailResult);
    }

    /// <inheritdoc />
    public Task<TmdbPage<TmdbTitleSummary>> SearchAsync(MediaType media, string query, int page, CancellationToken cancellationToken)
    {
        SearchCount++;
        return Task.FromResult(SearchResult);
    }

    /// <inheritdoc />
    public Task<TmdbTitleDetails?> GetDetailsAsync(MediaType media, int tmdbId, CancellationToken cancellationToken)
    {
        GetDetailsCount++;
        return Task.FromResult(DetailsResult);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbGenre>> GetGenresAsync(MediaType media, CancellationToken cancellationToken)
    {
        GetGenresCount++;
        return Task.FromResult(GenresResult);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbTitleSummary>> GetRecommendationsAsync(MediaType media, int tmdbId, CancellationToken cancellationToken)
    {
        GetRecommendationsCount++;
        return Task.FromResult(RailResult);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbTitleSummary>> DiscoverByGenreAsync(MediaType media, IReadOnlyList<int> genreTmdbIds, CancellationToken cancellationToken)
    {
        DiscoverCount++;
        return Task.FromResult(RailResult);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbTitleSummary>> GetRegionalRailAsync(MediaType media, RailKind kind, string originalLanguage, CancellationToken cancellationToken)
    {
        GetRegionalCount++;
        return Task.FromResult(RailResult);
    }

    /// <summary>Builds a summary read model with the given id and title.</summary>
    public static TmdbTitleSummary Summary(int tmdbId, string title) => new()
    {
        TmdbId = tmdbId,
        MediaType = MediaType.Movie,
        Title = title,
        Overview = "An overview.",
        VoteAverage = 8.4,
        VoteCount = 100,
        GenreIds = [28, 878],
    };

    /// <summary>Builds a details read model with the given id and title.</summary>
    public static TmdbTitleDetails Details(int tmdbId, string title) => new()
    {
        TmdbId = tmdbId,
        MediaType = MediaType.Movie,
        Title = title,
        Runtime = 148,
        VoteAverage = 8.4,
        VoteCount = 100,
        Genres = [new TmdbGenre { TmdbGenreId = 28, Name = "Action" }],
    };

    /// <summary>Builds a single-item search page with the given id and title.</summary>
    public static TmdbPage<TmdbTitleSummary> Page(int tmdbId, string title) => new()
    {
        Page = 1,
        TotalPages = 1,
        TotalResults = 1,
        Items = [Summary(tmdbId, title)],
    };

    /// <summary>Builds an empty (zero-result) search page — a search miss.</summary>
    public static TmdbPage<TmdbTitleSummary> EmptyPage() => new()
    {
        Page = 1,
        TotalPages = 0,
        TotalResults = 0,
        Items = [],
    };
}
