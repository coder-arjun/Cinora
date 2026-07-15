using Cinora.Application.Features.Watchlist;
using Cinora.Application.Tests.Common.Fakes;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;

namespace Cinora.Application.Tests.Features.Watchlists;

/// <summary>
/// Milestone 4.4 unit tests for <see cref="GetMyWatchlistQueryHandler"/> — the owner-scoped, keyset-paginated
/// watchlist page (§6.1) — against an in-process SQLite <see cref="TestAppDbContext"/>. They cover the FIRST
/// page (no cursor): the query returns only the current user's entries newest-added first, joins the title by
/// PK into the projection (N+1-free), scopes to a status filter, and reports <c>HasMore</c> + a
/// <see cref="WatchlistCursor"/> when the list exceeds the page size. The cursor-continuation / no-overlap path
/// (which uses a <c>Guid.CompareTo</c> keyset predicate the production SQL Server translates) is proven end-to-
/// end in <c>WatchlistPageTests</c> against the real database.
/// </summary>
public sealed class GetMyWatchlistQueryHandlerTests
{
    // The first page shows only my entries, newest-added first, with the title joined in (title/poster/year).
    [Fact]
    public async Task Handle_returns_only_my_entries_newest_first_with_the_title_joined()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();

        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        var older = AddTitleAndEntry(db, me, 800_001, "Older Title", "/older.jpg", new DateTime(2001, 5, 1),
            WatchlistStatus.PlanToWatch, addedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified));
        var newer = AddTitleAndEntry(db, me, 800_002, "Newer Title", "/newer.jpg", new DateTime(2010, 8, 1),
            WatchlistStatus.Watched, addedAt: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Unspecified));
        // Another user's entry — must never appear in my page.
        AddTitleAndEntry(db, other, 800_003, "Not Mine", null, null, WatchlistStatus.Watching,
            addedAt: new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Unspecified));
        await db.SaveChangesAsync();

        var handler = new GetMyWatchlistQueryHandler(db, new StubCurrentUser(me));

        var pageVm = await handler.Handle(new GetMyWatchlistQuery(Filter: null), CancellationToken.None);

        Assert.False(pageVm.HasMore);
        Assert.Null(pageVm.NextCursor);
        Assert.Equal(2, pageVm.Items.Count);

        // Newest-added first, and only my two entries (the other user's title never appears).
        Assert.Equal(newer, pageVm.Items[0].MovieId);
        Assert.Equal(older, pageVm.Items[1].MovieId);
        Assert.DoesNotContain(pageVm.Items, item => item.TmdbId == 800_003);

        // The title join populated the projection (no N+1, no null title).
        var top = pageVm.Items[0];
        Assert.Equal("Newer Title", top.Title);
        Assert.Equal("/newer.jpg", top.PosterPath);
        Assert.Equal(2010, top.ReleaseYear);
        Assert.Equal(800_002, top.TmdbId);
        Assert.Equal(WatchlistStatus.Watched, top.Status);
    }

    // A status filter scopes the page to that status only.
    [Fact]
    public async Task Handle_filters_the_page_by_status()
    {
        var me = Guid.NewGuid();

        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        AddTitleAndEntry(db, me, 801_001, "Plan A", null, null, WatchlistStatus.PlanToWatch,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified));
        var watchedId = AddTitleAndEntry(db, me, 801_002, "Watched B", null, null, WatchlistStatus.Watched,
            new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Unspecified));
        await db.SaveChangesAsync();

        var handler = new GetMyWatchlistQueryHandler(db, new StubCurrentUser(me));

        var pageVm = await handler.Handle(
            new GetMyWatchlistQuery(Filter: WatchlistStatus.Watched), CancellationToken.None);

        Assert.Single(pageVm.Items);
        Assert.Equal(watchedId, pageVm.Items[0].MovieId);
        Assert.Equal(WatchlistStatus.Watched, pageVm.Items[0].Status);
    }

    // Take + 1 detection: more entries than the page size → HasMore true and a non-null NextCursor.
    [Fact]
    public async Task Handle_reports_more_and_a_cursor_when_the_list_exceeds_the_page_size()
    {
        var me = Guid.NewGuid();

        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        for (var i = 0; i < 3; i++)
        {
            AddTitleAndEntry(db, me, 802_000 + i, $"Title {i}", null, null, WatchlistStatus.PlanToWatch,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMinutes(i));
        }
        await db.SaveChangesAsync();

        var handler = new GetMyWatchlistQueryHandler(db, new StubCurrentUser(me));

        // Page size 2 over 3 entries → the 2 newest are returned and HasMore is true.
        var pageVm = await handler.Handle(new GetMyWatchlistQuery(Filter: null, Take: 2), CancellationToken.None);

        Assert.True(pageVm.HasMore);
        Assert.Equal(2, pageVm.Items.Count);
        Assert.NotNull(pageVm.NextCursor);
        Assert.Equal(pageVm.Items[^1].AddedAtUtc, pageVm.NextCursor!.SortKey);
        Assert.Equal(pageVm.Items[^1].EntryId, pageVm.NextCursor.Id);
    }

    // Seeds a Movie + one of the given user's watchlist entries for it, stamping an explicit AddedAtUtc so the
    // keyset ordering is deterministic. Returns the internal Movie id.
    private static Guid AddTitleAndEntry(
        TestAppDbContext db,
        Guid userId,
        int tmdbId,
        string title,
        string? posterPath,
        DateTime? releaseDate,
        WatchlistStatus status,
        DateTime addedAt)
    {
        var movie = Movie.FromTmdb(tmdbId, MediaType.Movie, title, null, releaseDate, posterPath, null);
        db.Movies.Add(movie);

        var entry = Watchlist.Add(userId, movie.Id, status);
        db.Watchlists.Add(entry);
        db.Entry(entry).Property(nameof(Watchlist.AddedAtUtc)).CurrentValue = addedAt;

        return movie.Id;
    }
}
