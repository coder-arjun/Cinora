using Cinora.Application.Features.Watchlist;
using Cinora.Application.Tests.Common.Fakes;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;

namespace Cinora.Application.Tests.Features.Watchlists;

/// <summary>
/// Milestone 4.4 unit tests for <see cref="GetWatchlistCountsQueryHandler"/> — the per-status filter-chip counts
/// (§6.1) — against an in-process SQLite <see cref="TestAppDbContext"/> (a real relational engine that runs the
/// actual <c>GROUP BY Status</c>). They prove the counts are scoped to the current user (another user's entries
/// never count), that each status total and the "All" total are correct, and that a user with no entries yields
/// an all-zero view.
/// </summary>
public sealed class GetWatchlistCountsQueryHandlerTests
{
    // Counts are per-status, total is their sum, and another user's entries never leak into my counts.
    [Fact]
    public async Task Handle_counts_my_entries_per_status_and_excludes_other_users()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();

        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        // My list: 2 PlanToWatch, 1 Watching, 3 Watched (each on a distinct MovieId so the unique index holds).
        AddEntries(db, me, WatchlistStatus.PlanToWatch, 2);
        AddEntries(db, me, WatchlistStatus.Watching, 1);
        AddEntries(db, me, WatchlistStatus.Watched, 3);
        // Another user's entries — must never count toward mine.
        AddEntries(db, other, WatchlistStatus.PlanToWatch, 5);
        AddEntries(db, other, WatchlistStatus.Watched, 4);
        await db.SaveChangesAsync();

        var handler = new GetWatchlistCountsQueryHandler(db, new StubCurrentUser(me));

        var counts = await handler.Handle(new GetWatchlistCountsQuery(), CancellationToken.None);

        Assert.Equal(2, counts.PlanToWatch);
        Assert.Equal(1, counts.Watching);
        Assert.Equal(3, counts.Watched);
        Assert.Equal(6, counts.Total);

        // CountFor mirrors the chips: a status → its count, null ("All") → the total.
        Assert.Equal(2, counts.CountFor(WatchlistStatus.PlanToWatch));
        Assert.Equal(6, counts.CountFor(null));
    }

    // A user with no entries yields all zeros (drives the totally-empty page state), not an error.
    [Fact]
    public async Task Handle_no_entries_returns_all_zero()
    {
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        var handler = new GetWatchlistCountsQueryHandler(db, new StubCurrentUser(Guid.NewGuid()));

        var counts = await handler.Handle(new GetWatchlistCountsQuery(), CancellationToken.None);

        Assert.Equal(0, counts.Total);
        Assert.Equal(0, counts.PlanToWatch);
        Assert.Equal(0, counts.Watching);
        Assert.Equal(0, counts.Watched);
    }

    private static void AddEntries(TestAppDbContext db, Guid userId, WatchlistStatus status, int count)
    {
        for (var i = 0; i < count; i++)
        {
            db.Watchlists.Add(Watchlist.Add(userId, Guid.NewGuid(), status));
        }
    }
}
