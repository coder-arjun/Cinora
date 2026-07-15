using System.Net;
using Cinora.Domain.Enums;
using Cinora.Infrastructure.Tmdb;

namespace Cinora.Infrastructure.Tests.Tmdb;

/// <summary>
/// Verifies the TMDB adapter (<see cref="TmdbClient"/>) deserializes recorded TMDB JSON fixtures and maps
/// them to Application read models — ids, titles, media types, dates, genres, cast, and paging — and that
/// it builds the correct request URLs and returns <c>null</c> on a 404. Runs entirely against a fake
/// <see cref="StubHttpMessageHandler"/>: no live network, no TMDB key.
/// </summary>
public sealed class TmdbClientTests
{
    private static readonly Uri BaseAddress = new("https://api.themoviedb.org/3/");

    // Extracted to a static field to satisfy CA1861 (constant array argument in a repeatedly-called assert).
    private static readonly int[] ExpectedInceptionGenreIds = [28, 878, 12];

    private static TmdbClient CreateClient(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler, disposeHandler: false) { BaseAddress = BaseAddress });

    [Fact]
    public async Task GetTrendingAsync_maps_movie_list_to_summaries_and_hits_the_trending_endpoint()
    {
        using var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, TmdbFixtures.Load("movie_list.json"));
        var client = CreateClient(handler);

        var summaries = await client.GetTrendingAsync(MediaType.Movie, CancellationToken.None);

        Assert.Equal(2, summaries.Count);

        var inception = summaries[0];
        Assert.Equal(27205, inception.TmdbId);
        Assert.Equal(MediaType.Movie, inception.MediaType);
        Assert.Equal("Inception", inception.Title);
        Assert.Equal("/inception_poster.jpg", inception.PosterPath);
        Assert.Equal("/inception_backdrop.jpg", inception.BackdropPath);
        Assert.Equal(new DateOnly(2010, 7, 15), inception.ReleaseDate);
        Assert.Equal(8.369, inception.VoteAverage, 3);
        Assert.Equal(36000, inception.VoteCount);
        Assert.Equal(ExpectedInceptionGenreIds, inception.GenreIds);

        // A missing backdrop_path maps to null rather than an empty string.
        Assert.Null(summaries[1].BackdropPath);

        Assert.Contains("trending/movie/week", handler.LastRequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTrendingAsync_maps_series_name_and_first_air_date_for_a_tv_list()
    {
        using var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, TmdbFixtures.Load("tv_list.json"));
        var client = CreateClient(handler);

        var summaries = await client.GetTrendingAsync(MediaType.Series, CancellationToken.None);

        var breakingBad = Assert.Single(summaries);
        Assert.Equal(1396, breakingBad.TmdbId);
        Assert.Equal(MediaType.Series, breakingBad.MediaType);
        Assert.Equal("Breaking Bad", breakingBad.Title); // TMDB "name" for series
        Assert.Equal(new DateOnly(2008, 1, 20), breakingBad.ReleaseDate); // TMDB "first_air_date" for series
        Assert.Contains("trending/tv/week", handler.LastRequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPopularAsync_maps_results_and_hits_the_popular_endpoint()
    {
        using var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, TmdbFixtures.Load("movie_list.json"));
        var client = CreateClient(handler);

        var summaries = await client.GetPopularAsync(MediaType.Movie, CancellationToken.None);

        Assert.Equal(2, summaries.Count);
        Assert.Contains("movie/popular", handler.LastRequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTopRatedAsync_maps_results_and_hits_the_top_rated_endpoint()
    {
        using var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, TmdbFixtures.Load("movie_list.json"));
        var client = CreateClient(handler);

        var summaries = await client.GetTopRatedAsync(MediaType.Movie, CancellationToken.None);

        Assert.Equal(2, summaries.Count);
        Assert.Contains("movie/top_rated", handler.LastRequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_maps_paging_metadata_and_encodes_the_query()
    {
        using var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, TmdbFixtures.Load("search_movie.json"));
        var client = CreateClient(handler);

        var page = await client.SearchAsync(MediaType.Movie, "dark knight", 2, CancellationToken.None);

        Assert.Equal(2, page.Page);
        Assert.Equal(7, page.TotalPages);
        Assert.Equal(132, page.TotalResults);

        var result = Assert.Single(page.Items);
        Assert.Equal(155, result.TmdbId);
        Assert.Equal("The Dark Knight", result.Title);

        // AbsoluteUri preserves percent-encoding (ToString() unescapes %20 to a space for display).
        var requestUri = handler.LastRequestUri!.AbsoluteUri;
        Assert.Contains("search/movie", requestUri, StringComparison.Ordinal);
        Assert.Contains("query=dark%20knight", requestUri, StringComparison.Ordinal);
        Assert.Contains("page=2", requestUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDetailsAsync_maps_movie_details_genres_and_ranks_cast_by_billing_order()
    {
        using var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, TmdbFixtures.Load("movie_details.json"));
        var client = CreateClient(handler);

        var details = await client.GetDetailsAsync(MediaType.Movie, 27205, CancellationToken.None);

        Assert.NotNull(details);
        Assert.Equal(27205, details!.TmdbId);
        Assert.Equal(MediaType.Movie, details.MediaType);
        Assert.Equal("Inception", details.Title);
        Assert.Equal("Your mind is the scene of the crime.", details.Tagline);
        Assert.Equal(148, details.Runtime);
        Assert.Equal("/inception_backdrop.jpg", details.BackdropPath);

        Assert.Equal(3, details.Genres.Count);
        Assert.Equal(28, details.Genres[0].TmdbGenreId);
        Assert.Equal("Action", details.Genres[0].Name);

        // The fixture lists cast out of billing order (2, 0, 1); the mapper ranks them ascending.
        Assert.Equal(3, details.Cast.Count);
        Assert.Equal("Leonardo DiCaprio", details.Cast[0].Name);
        Assert.Equal(0, details.Cast[0].Order);
        Assert.Equal("Joseph Gordon-Levitt", details.Cast[1].Name);
        Assert.Equal("Elliot Page", details.Cast[2].Name);
        Assert.Null(details.Cast[2].ProfilePath); // null profile_path maps to null

        Assert.Contains("append_to_response=credits", handler.LastRequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDetailsAsync_maps_series_name_first_air_date_and_first_episode_run_time()
    {
        using var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, TmdbFixtures.Load("tv_details.json"));
        var client = CreateClient(handler);

        var details = await client.GetDetailsAsync(MediaType.Series, 1396, CancellationToken.None);

        Assert.NotNull(details);
        Assert.Equal(MediaType.Series, details!.MediaType);
        Assert.Equal("Breaking Bad", details.Title); // TMDB "name"
        Assert.Equal(new DateOnly(2008, 1, 20), details.ReleaseDate); // TMDB "first_air_date"
        Assert.Equal(45, details.Runtime); // first of episode_run_time [45, 47]
        Assert.Equal(2, details.Genres.Count);
        Assert.Equal("Bryan Cranston", Assert.Single(details.Cast).Name);
    }

    [Fact]
    public async Task GetDetailsAsync_returns_null_when_tmdb_responds_404()
    {
        using var handler = StubHttpMessageHandler.Json(
            HttpStatusCode.NotFound,
            "{\"status_code\":34,\"status_message\":\"The resource you requested could not be found.\"}");
        var client = CreateClient(handler);

        var details = await client.GetDetailsAsync(MediaType.Movie, 999_999_999, CancellationToken.None);

        Assert.Null(details);
    }

    [Fact]
    public async Task GetGenresAsync_maps_the_genre_list_and_hits_the_genre_endpoint()
    {
        using var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, TmdbFixtures.Load("genres_movie.json"));
        var client = CreateClient(handler);

        var genres = await client.GetGenresAsync(MediaType.Movie, CancellationToken.None);

        Assert.Equal(4, genres.Count);
        Assert.Equal(28, genres[0].TmdbGenreId);
        Assert.Equal("Action", genres[0].Name);
        Assert.Contains("genre/movie/list", handler.LastRequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRecommendationsAsync_maps_results_and_hits_the_recommendations_endpoint()
    {
        using var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, TmdbFixtures.Load("movie_list.json"));
        var client = CreateClient(handler);

        var summaries = await client.GetRecommendationsAsync(MediaType.Movie, 27205, CancellationToken.None);

        Assert.Equal(2, summaries.Count);
        Assert.Contains("movie/27205/recommendations", handler.LastRequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverByGenreAsync_joins_genres_with_OR_and_hits_the_discover_endpoint()
    {
        using var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, TmdbFixtures.Load("movie_list.json"));
        var client = CreateClient(handler);
        int[] genreIds = [28, 878];

        var summaries = await client.DiscoverByGenreAsync(MediaType.Movie, genreIds, CancellationToken.None);

        Assert.Equal(2, summaries.Count);
        // Decode so the assertion is encoding-agnostic: '|' may travel as %7C on the wire. TMDB's with_genres
        // uses '|' for OR ("any of") — a comma would mean AND ("all of"), which is the H-1 regression this locks out.
        var requestUri = Uri.UnescapeDataString(handler.LastRequestUri!.AbsoluteUri);
        Assert.Contains("discover/movie", requestUri, StringComparison.Ordinal);
        Assert.Contains("with_genres=28|878", requestUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverByGenreAsync_returns_empty_without_a_call_when_no_genres_supplied()
    {
        using var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, TmdbFixtures.Load("movie_list.json"));
        var client = CreateClient(handler);
        int[] noGenres = [];

        var summaries = await client.DiscoverByGenreAsync(MediaType.Movie, noGenres, CancellationToken.None);

        Assert.Empty(summaries);
        Assert.Equal(0, handler.CallCount); // an unfiltered discover would be a generic popularity list — never issued
        Assert.Null(handler.LastRequestUri);
    }
}
