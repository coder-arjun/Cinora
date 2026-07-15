using Cinora.Application.Features.Watchlist;
using Cinora.Application.Tests.Common.Fakes;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Tests.Features.Watchlists;

/// <summary>
/// Milestone 4.3 unit tests for <see cref="SetWatchlistStatusCommandHandler"/>'s upsert semantics — add,
/// in-place update, and the unique <c>(UserId, MovieId)</c> insert-race reconcile (ADR 0014) — against an
/// in-process SQLite <see cref="TestAppDbContext"/>. SQLite really enforces the unique index, so the race path
/// throws a genuine <see cref="DbUpdateException"/> the shared-database integration suite cannot force
/// deterministically (the same rationale as the catalog race tests). The end-to-end persist-then-upsert is
/// covered in <c>WatchlistTests</c> (W1).
/// </summary>
public sealed class SetWatchlistStatusCommandHandlerTests
{
    private static readonly Guid MovieId = Guid.NewGuid();

    // Absent → a new entry is added (WasAdded true).
    [Fact]
    public async Task Handle_absent_entry_adds_it()
    {
        var userId = Guid.NewGuid();
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();
        var handler = new SetWatchlistStatusCommandHandler(db, new StubCurrentUser(userId));

        var result = await handler.Handle(
            new SetWatchlistStatusCommand(MovieId, WatchlistStatus.PlanToWatch), CancellationToken.None);

        Assert.True(result.WasAdded);
        Assert.Equal(WatchlistStatus.PlanToWatch, result.Status);
        var row = await db.Watchlists.AsNoTracking().SingleAsync(w => w.UserId == userId && w.MovieId == MovieId);
        Assert.Equal(WatchlistStatus.PlanToWatch, row.Status);
        Assert.Null(row.UpdatedAtUtc);
    }

    // Present → the SAME row is updated in place (WasAdded false, no duplicate; UpdatedAtUtc stamped).
    [Fact]
    public async Task Handle_existing_entry_updates_in_place_without_duplicating()
    {
        var userId = Guid.NewGuid();
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        db.Watchlists.Add(Watchlist.Add(userId, MovieId, WatchlistStatus.PlanToWatch));
        await db.SaveChangesAsync();

        var handler = new SetWatchlistStatusCommandHandler(db, new StubCurrentUser(userId));
        var result = await handler.Handle(
            new SetWatchlistStatusCommand(MovieId, WatchlistStatus.Watched), CancellationToken.None);

        Assert.False(result.WasAdded);
        Assert.Equal(WatchlistStatus.Watched, result.Status);
        Assert.Equal(1, await db.Watchlists.CountAsync(w => w.UserId == userId && w.MovieId == MovieId));
        var row = await db.Watchlists.AsNoTracking().SingleAsync(w => w.UserId == userId && w.MovieId == MovieId);
        Assert.Equal(WatchlistStatus.Watched, row.Status);
        Assert.NotNull(row.UpdatedAtUtc);
    }

    // A concurrent insert trips the unique index on our add; the handler discards the staged insert, re-probes
    // the winner, and updates it — no rethrow, no duplicate (the ADR-0014 reconcile mirrors CreateReview).
    [Fact]
    public async Task Handle_insert_race_reconciles_to_an_update()
    {
        var userId = Guid.NewGuid();
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        // Our add save is raced: a sibling commits the (userId, MovieId) row as PlanToWatch first, so the real
        // SQLite save trips the unique index and rolls our insert back. One-shot — the reconcile save then runs.
        db.RaceOnNextSave = () =>
        {
            using var concurrent = store.CreateContext();
            concurrent.Watchlists.Add(Watchlist.Add(userId, MovieId, WatchlistStatus.PlanToWatch));
            concurrent.SaveChanges();
        };

        var handler = new SetWatchlistStatusCommandHandler(db, new StubCurrentUser(userId));

        var result = await handler.Handle(
            new SetWatchlistStatusCommand(MovieId, WatchlistStatus.Watched), CancellationToken.None);

        // The DbUpdateException must NOT surface: the raced row is reloaded and updated to our status.
        Assert.False(result.WasAdded);
        Assert.Equal(WatchlistStatus.Watched, result.Status);
        Assert.Equal(1, await db.Watchlists.CountAsync(w => w.UserId == userId && w.MovieId == MovieId));
        var row = await db.Watchlists.AsNoTracking().SingleAsync(w => w.UserId == userId && w.MovieId == MovieId);
        Assert.Equal(WatchlistStatus.Watched, row.Status);
    }
}
