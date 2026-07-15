using Cinora.Application.Features.Recommendations;
using Cinora.Application.Tests.Common.Fakes;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Domain.ValueObjects;

namespace Cinora.Application.Tests.Features.Recommendations;

/// <summary>
/// Milestone 5.1 unit tests for <see cref="UserTasteProfileBuilder"/> — the taste projection that grounds AI
/// recommendations (ADR 0017 §1, design §5.1) — against an in-process SQLite <see cref="TestAppDbContext"/>.
/// They prove the profile is scoped to the acting user (a second user's reviews/watchlist never leak in), the
/// top-rated titles are ordered by score with their genres resolved, watchlist titles and favorite genres are
/// projected, the seen-exclusion set covers reviewed AND watchlisted titles, and a user with no signal is a
/// cold-start (<see cref="UserTasteResult.HasSignal"/> false).
/// </summary>
public sealed class UserTasteProfileBuilderTests
{
    private const int ActionGenre = 28;
    private const int DramaGenre = 18;
    private const int SciFiGenre = 878;

    [Fact]
    public async Task BuildAsync_projects_only_my_taste_scoped_by_user()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();

        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        var action = SeedGenre(db, ActionGenre, "Action");
        var drama = SeedGenre(db, DramaGenre, "Drama");
        var sciFi = SeedGenre(db, SciFiGenre, "Science Fiction");

        // Mine: two reviews (rating 9 then 7) + one plan-to-watch entry. Action appears on two of my titles so it
        // is the most-frequent genre; Sci-Fi and Drama appear once each.
        var top = SeedMovie(db, 500_001, "Top Movie", new DateTime(2014, 11, 7), action, sciFi);
        SeedReview(db, me, top, rating: 9);

        var second = SeedMovie(db, 500_002, "Second Movie", new DateTime(2019, 5, 1), drama);
        SeedReview(db, me, second, rating: 7);

        var planned = SeedMovie(db, 500_003, "Planned Movie", new DateTime(2021, 1, 1), action);
        SeedWatchlist(db, me, planned, WatchlistStatus.PlanToWatch);

        // A stranger's data — must never appear anywhere in my profile.
        var strangerTitle = SeedMovie(db, 500_004, "Stranger Movie", new DateTime(2000, 1, 1), action);
        SeedReview(db, other, strangerTitle, rating: 10);

        await db.SaveChangesAsync();

        var builder = new UserTasteProfileBuilder(db, new StubRecommendationPolicy());

        var result = await builder.BuildAsync(me, CancellationToken.None);

        Assert.True(result.HasSignal);

        // Top-rated: best score first, with genres resolved from the MovieGenre → Genre map.
        Assert.Equal(2, result.Profile.TopRated.Count);
        Assert.Equal("Top Movie", result.Profile.TopRated[0].Title);
        Assert.Equal(9, result.Profile.TopRated[0].Rating);
        Assert.Equal(2014, result.Profile.TopRated[0].Year);
        Assert.Contains("Action", result.Profile.TopRated[0].Genres);
        Assert.Contains("Science Fiction", result.Profile.TopRated[0].Genres);
        Assert.Equal("Second Movie", result.Profile.TopRated[1].Title);
        Assert.Equal(7, result.Profile.TopRated[1].Rating);

        // Watchlist interest (plan-to-watch) is projected; the stranger's title is absent.
        Assert.Equal(["Planned Movie"], result.Profile.WatchlistTitles);
        Assert.DoesNotContain("Stranger Movie", result.Profile.WatchlistTitles);

        // Favorite genres by frequency across my rated + watchlisted movies → Action (2) is first.
        Assert.Equal("Action", result.Profile.FavoriteGenres[0]);
        Assert.Equal(ActionGenre, result.FavoriteGenreTmdbIds[0]);

        // Seed keys are my highest-rated titles' TMDB keys (for the "more like this" fan-out).
        Assert.Contains((500_001, MediaType.Movie), result.SeedKeys);
        Assert.Contains((500_002, MediaType.Movie), result.SeedKeys);

        // Seen exclusions cover reviewed AND watchlisted titles; the stranger's title is not excluded (not mine).
        Assert.Contains((500_001, MediaType.Movie), result.SeenExclusions);
        Assert.Contains((500_002, MediaType.Movie), result.SeenExclusions);
        Assert.Contains((500_003, MediaType.Movie), result.SeenExclusions);
        Assert.DoesNotContain((500_004, MediaType.Movie), result.SeenExclusions);
    }

    [Fact]
    public async Task BuildAsync_respects_the_top_rated_and_seed_caps()
    {
        var me = Guid.NewGuid();

        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        // Six reviews with descending ratings 10..5 so the order is deterministic.
        for (var i = 0; i < 6; i++)
        {
            var movie = SeedMovie(db, 510_000 + i, $"Rated {i}", new DateTime(2010 + i, 1, 1));
            SeedReview(db, me, movie, rating: 10 - i);
        }

        await db.SaveChangesAsync();

        var policy = new StubRecommendationPolicy { MaxTopRated = 3, MaxRecommendationSeeds = 2 };
        var builder = new UserTasteProfileBuilder(db, policy);

        var result = await builder.BuildAsync(me, CancellationToken.None);

        // Top-rated is capped and ordered by score desc: 10, 9, 8.
        Assert.Equal(3, result.Profile.TopRated.Count);
        Assert.Equal(10, result.Profile.TopRated[0].Rating);
        Assert.Equal(9, result.Profile.TopRated[1].Rating);
        Assert.Equal(8, result.Profile.TopRated[2].Rating);

        // Seed keys are capped to the highest-rated two.
        Assert.Equal(2, result.SeedKeys.Count);
        Assert.Equal((510_000, MediaType.Movie), result.SeedKeys[0]); // rating 10
        Assert.Equal((510_001, MediaType.Movie), result.SeedKeys[1]); // rating 9
    }

    [Fact]
    public async Task BuildAsync_returns_a_thin_profile_for_a_user_with_no_signal()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();

        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        // Only the OTHER user has any data; `me` has neither a review nor a watchlist entry.
        var movie = SeedMovie(db, 520_001, "Not Mine", new DateTime(2015, 1, 1));
        SeedReview(db, other, movie, rating: 8);
        await db.SaveChangesAsync();

        var builder = new UserTasteProfileBuilder(db, new StubRecommendationPolicy());

        var result = await builder.BuildAsync(me, CancellationToken.None);

        Assert.False(result.HasSignal);
        Assert.Empty(result.Profile.TopRated);
        Assert.Empty(result.Profile.WatchlistTitles);
        Assert.Empty(result.Profile.FavoriteGenres);
        Assert.Empty(result.SeedKeys);
        Assert.Empty(result.FavoriteGenreTmdbIds);
        Assert.Empty(result.SeenExclusions);
    }

    private static Guid SeedGenre(TestAppDbContext db, int tmdbGenreId, string name)
    {
        var genre = Genre.Create(tmdbGenreId, name);
        db.Genres.Add(genre);
        return genre.Id;
    }

    private static Guid SeedMovie(
        TestAppDbContext db, int tmdbId, string title, DateTime releaseDate, params Guid[] genreIds)
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

    private static void SeedWatchlist(TestAppDbContext db, Guid userId, Guid movieId, WatchlistStatus status) =>
        db.Watchlists.Add(Watchlist.Add(userId, movieId, status));
}
