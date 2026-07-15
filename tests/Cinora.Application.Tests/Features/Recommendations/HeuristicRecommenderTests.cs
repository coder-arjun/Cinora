using Cinora.Application.Common.Tmdb;
using Cinora.Application.Features.Recommendations;
using Cinora.Application.Tests.Common.Fakes;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinora.Application.Tests.Features.Recommendations;

/// <summary>
/// Unit tests for <see cref="HeuristicRecommender"/> — the deterministic, never-500, no-LLM fallback (Milestone
/// 5.2, ADR 0018 §4 tier 3, design §9) — driven by the real <see cref="UserTasteProfileBuilder"/> over an
/// in-process SQLite <see cref="TestAppDbContext"/> and a canned <see cref="RecommendationTmdbClientStub"/> (no
/// network, no model). They prove the guarantees the serve path relies on: seen titles are excluded, the pool is
/// capped to <c>MaxResults</c>, a brand-new user still gets popular picks (non-empty), and each tier carries an
/// honest template reason. The engine is not a dependency, so "zero LLM calls" holds by construction.
/// </summary>
public sealed class HeuristicRecommenderTests
{
    private const int ActionGenre = 28;

    [Fact]
    public async Task RecommendAsync_excludes_seen_titles_and_uses_an_honest_seed_reason()
    {
        var me = Guid.NewGuid();
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        var action = SeedGenre(db, ActionGenre, "Action");
        var seed = SeedMovie(db, 800_001, "Seed Movie", new DateTime(2014, 11, 7), action);
        SeedReview(db, me, seed, rating: 9);
        await db.SaveChangesAsync();

        var tmdb = new RecommendationTmdbClientStub();
        tmdb.RecommendationsByTitle[(MediaType.Movie, 800_001)] =
        [
            Summary(800_010, "Recommended X"),
            Summary(800_001, "Seed echoed back"), // the SEEN title — must be excluded
        ];

        var result = await NewRecommender(db, new StubRecommendationPolicy(), tmdb).RecommendAsync(me, CancellationToken.None);

        Assert.Equal(RecommendationSource.Heuristic, result.Source);
        var pick = Assert.Single(result.Picks);
        Assert.Equal(800_010, pick.TmdbId);                             // the new title survived
        Assert.DoesNotContain(result.Picks, p => p.TmdbId == 800_001);  // the seen title was excluded
        Assert.Equal("Because you rated Seed Movie 9/10.", pick.Reason); // honest, seed-derived reason
        Assert.Equal("Recommended X", pick.Title);                      // card metadata from the TMDB summary
    }

    [Fact]
    public async Task RecommendAsync_uses_the_favorite_genre_reason_for_the_discover_tier()
    {
        var me = Guid.NewGuid();
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        var action = SeedGenre(db, ActionGenre, "Action");
        var seed = SeedMovie(db, 801_001, "My Favourite", new DateTime(2016, 1, 1), action);
        SeedReview(db, me, seed, rating: 8);
        await db.SaveChangesAsync();

        var tmdb = new RecommendationTmdbClientStub();
        // No "more like this" results, so the discover tier is what supplies the picks.
        tmdb.DiscoverByMediaType[MediaType.Movie] = [Summary(801_010, "Genre Pick")];

        var result = await NewRecommender(db, new StubRecommendationPolicy(), tmdb).RecommendAsync(me, CancellationToken.None);

        var pick = Assert.Single(result.Picks);
        Assert.Equal(801_010, pick.TmdbId);
        Assert.Equal("Popular in Action.", pick.Reason);
    }

    [Fact]
    public async Task RecommendAsync_caps_the_set_to_MaxResults()
    {
        var me = Guid.NewGuid();
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();
        // No taste at all → a brand-new user, so the popular top-up drives the whole set.

        var tmdb = new RecommendationTmdbClientStub
        {
            PopularByMediaType = { [MediaType.Movie] = Enumerable.Range(1, 20).Select(i => Summary(802_000 + i, $"Pop {i}")).ToList() },
        };
        var policy = new StubRecommendationPolicy { MaxResults = 3 };

        var result = await NewRecommender(db, policy, tmdb).RecommendAsync(me, CancellationToken.None);

        Assert.Equal(3, result.Picks.Count);
    }

    [Fact]
    public async Task RecommendAsync_returns_popular_picks_for_a_brand_new_user()
    {
        var me = Guid.NewGuid();
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext(); // `me` has neither a review nor a watchlist entry

        var tmdb = new RecommendationTmdbClientStub
        {
            PopularByMediaType = { [MediaType.Movie] = [Summary(803_050, "Popular Blockbuster")] },
        };

        var result = await NewRecommender(db, new StubRecommendationPolicy(), tmdb).RecommendAsync(me, CancellationToken.None);

        Assert.Equal(RecommendationSource.Heuristic, result.Source);
        var pick = Assert.Single(result.Picks);
        Assert.Equal(803_050, pick.TmdbId);
        Assert.Equal("Popular on Cinora right now.", pick.Reason);
    }

    [Fact]
    public async Task RecommendAsync_returns_a_heuristic_set_without_throwing_when_every_tmdb_call_fails()
    {
        var me = Guid.NewGuid();
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        // A user WITH taste (a review in a favorite genre) so all four fan-out tiers are exercised — then TMDB is
        // down for every one of them (the CachedTmdbClient re-throws). The heuristic must still honor its
        // "empty (or partial) set otherwise" contract instead of surfacing a 500.
        var action = SeedGenre(db, ActionGenre, "Action");
        var seed = SeedMovie(db, 804_001, "Down Seed", new DateTime(2015, 5, 1), action);
        SeedReview(db, me, seed, rating: 9);
        await db.SaveChangesAsync();

        var tmdb = new RecommendationTmdbClientStub
        {
            ThrowOnRecommendations = true,
            ThrowOnDiscover = true,
            ThrowOnPopular = true,
            ThrowOnTopRated = true,
        };

        var result = await NewRecommender(db, new StubRecommendationPolicy(), tmdb).RecommendAsync(me, CancellationToken.None);

        Assert.Equal(RecommendationSource.Heuristic, result.Source);
        Assert.Empty(result.Picks); // every tier failed → an empty (never a thrown) heuristic set
    }

    [Fact]
    public async Task RecommendAsync_preserves_earlier_tier_picks_when_a_later_tmdb_tier_fails()
    {
        var me = Guid.NewGuid();
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        var action = SeedGenre(db, ActionGenre, "Action");
        var seed = SeedMovie(db, 805_001, "Partial Seed", new DateTime(2016, 3, 1), action);
        SeedReview(db, me, seed, rating: 10);
        await db.SaveChangesAsync();

        // The FIRST tier (per-seed recommendations) succeeds; every LATER tier (discover / popular / top-rated)
        // throws. The earlier tier's pick must survive the mid-fan-out failure (partial-result proof).
        var tmdb = new RecommendationTmdbClientStub
        {
            ThrowOnDiscover = true,
            ThrowOnPopular = true,
            ThrowOnTopRated = true,
        };
        tmdb.RecommendationsByTitle[(MediaType.Movie, 805_001)] = [Summary(805_010, "Survived Pick")];

        var result = await NewRecommender(db, new StubRecommendationPolicy(), tmdb).RecommendAsync(me, CancellationToken.None);

        Assert.Equal(RecommendationSource.Heuristic, result.Source);
        var pick = Assert.Single(result.Picks);
        Assert.Equal(805_010, pick.TmdbId); // the first tier's title survived the later tiers' failures
    }

    private static HeuristicRecommender NewRecommender(
        TestAppDbContext db, StubRecommendationPolicy policy, RecommendationTmdbClientStub? tmdb = null) =>
        new(
            new UserTasteProfileBuilder(db, policy),
            tmdb ?? new RecommendationTmdbClientStub(),
            policy,
            TimeProvider.System,
            NullLogger<HeuristicRecommender>.Instance);

    private static Guid SeedGenre(TestAppDbContext db, int tmdbGenreId, string name)
    {
        var genre = Genre.Create(tmdbGenreId, name);
        db.Genres.Add(genre);
        return genre.Id;
    }

    private static Guid SeedMovie(TestAppDbContext db, int tmdbId, string title, DateTime releaseDate, params Guid[] genreIds)
    {
        var movie = Movie.FromTmdb(tmdbId, MediaType.Movie, title, null, releaseDate, "/poster.jpg", null);
        db.Movies.Add(movie);
        foreach (var genreId in genreIds)
        {
            db.MovieGenres.Add(MovieGenre.Link(movie.Id, genreId));
        }

        return movie.Id;
    }

    private static void SeedReview(TestAppDbContext db, Guid userId, Guid movieId, int rating) =>
        db.Reviews.Add(Review.Create(userId, movieId, Rating.From(rating), "A seeded review body."));

    private static TmdbTitleSummary Summary(int tmdbId, string title) =>
        new()
        {
            TmdbId = tmdbId,
            MediaType = MediaType.Movie,
            Title = title,
            PosterPath = "/summary.jpg",
            ReleaseDate = new DateOnly(2022, 1, 1),
        };
}
