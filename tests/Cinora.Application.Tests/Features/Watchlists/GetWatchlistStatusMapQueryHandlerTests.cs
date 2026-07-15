using Cinora.Application.Common.Interfaces;
using Cinora.Application.Features.Watchlist;
using Cinora.Application.Tests.Common.Fakes;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;

namespace Cinora.Application.Tests.Features.Watchlists;

/// <summary>
/// Milestone 4.3 (W3) unit tests for <see cref="GetWatchlistStatusMapQueryHandler"/> — the N+1-killing batch
/// status map (ADR 0014) — against an in-process SQLite <see cref="TestAppDbContext"/> (a genuine relational
/// engine that runs the real <c>Movies ⋈ Watchlists</c> join and enforces the unique indexes). They prove the
/// map returns ONLY the current user's statuses for persisted, in-list titles; that another user's entries
/// never leak; that unpersisted titles and titles of a different media type are absent; and that an anonymous
/// caller gets an empty map without a query.
/// </summary>
public sealed class GetWatchlistStatusMapQueryHandlerTests
{
    // W3 — the map is scoped to the current user and omits foreign, unpersisted, and cross-media titles.
    [Fact]
    public async Task Handle_returns_only_my_statuses_and_omits_foreign_unpersisted_and_cross_media_titles()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();

        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        // Persisted titles. Note 700_001 exists as BOTH a Movie and a Series (same TMDB id, distinct rows via
        // the unique (TmdbId, MediaType) index) — only the Movie one may appear in a Movie-media map.
        var planMovie = Movie.FromTmdb(700_001, MediaType.Movie, "Plan Title", null, null, null, null);
        var watchedMovie = Movie.FromTmdb(700_002, MediaType.Movie, "Watched Title", null, null, null, null);
        var othersMovie = Movie.FromTmdb(700_003, MediaType.Movie, "Others Title", null, null, null, null);
        var persistedNotInList = Movie.FromTmdb(700_004, MediaType.Movie, "Persisted, not listed", null, null, null, null);
        var sameIdSeries = Movie.FromTmdb(700_001, MediaType.Series, "Same id, series", null, null, null, null);
        db.Movies.AddRange(planMovie, watchedMovie, othersMovie, persistedNotInList, sameIdSeries);

        // My list: 700_001 PlanToWatch, 700_002 Watched. The series row (same TMDB id) is NOT mine.
        db.Watchlists.Add(Watchlist.Add(me, planMovie.Id, WatchlistStatus.PlanToWatch));
        db.Watchlists.Add(Watchlist.Add(me, watchedMovie.Id, WatchlistStatus.Watched));
        db.Watchlists.Add(Watchlist.Add(me, sameIdSeries.Id, WatchlistStatus.Watching)); // series → filtered out
        // Another user's entry for 700_003 — must never leak into my map.
        db.Watchlists.Add(Watchlist.Add(other, othersMovie.Id, WatchlistStatus.Watching));
        await db.SaveChangesAsync();

        var handler = new GetWatchlistStatusMapQueryHandler(db, new StubCurrentUser(me));

        // 700_005 was never persisted (no Movie row) → must be absent.
        var map = await handler.Handle(
            new GetWatchlistStatusMapQuery([700_001, 700_002, 700_003, 700_004, 700_005], MediaType.Movie),
            CancellationToken.None);

        Assert.Equal(2, map.Count);
        Assert.Equal(WatchlistStatus.PlanToWatch, map[700_001]); // the MOVIE row, not the series entry
        Assert.Equal(WatchlistStatus.Watched, map[700_002]);
        Assert.False(map.ContainsKey(700_003)); // another user's entry never leaks
        Assert.False(map.ContainsKey(700_004)); // persisted but not in my list
        Assert.False(map.ContainsKey(700_005)); // never persisted
    }

    // The anonymous / empty-page short-circuit: an empty map and no user id required.
    [Fact]
    public async Task Handle_anonymous_returns_an_empty_map()
    {
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        var handler = new GetWatchlistStatusMapQueryHandler(db, new AnonymousCurrentUser());

        var map = await handler.Handle(
            new GetWatchlistStatusMapQuery([1, 2, 3], MediaType.Movie), CancellationToken.None);

        Assert.Empty(map);
    }

    // A hand-rolled anonymous ICurrentUser (no id) — the only shape not covered by the shared StubCurrentUser.
    private sealed class AnonymousCurrentUser : ICurrentUser
    {
        public Guid? UserId => null;

        public bool IsAuthenticated => false;

        public Guid GetRequiredUserId() => throw new InvalidOperationException("Anonymous.");
    }
}
