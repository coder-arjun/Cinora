using Cinora.Application.Common.Tmdb;
using Cinora.Application.Features.Catalog;
using Cinora.Application.Tests.Common.Fakes;
using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinora.Application.Tests.Features.Catalog;

/// <summary>
/// Deterministic unit tests for <see cref="SyncGenresCommandHandler"/> against an in-process SQLite
/// <see cref="TestAppDbContext"/>: the ordinary insert/partition path plus the concurrent genre-insert race
/// (F1). The race case proves a <see cref="DbUpdateException"/> from the (SQLite-enforced) unique
/// <c>TmdbGenreId</c> index is reconciled — never surfaced — and that raced-away genres count as
/// already-present rather than inserted.
/// </summary>
public sealed class SyncGenresCommandHandlerTests
{
    [Fact]
    public async Task Handle_NoConcurrency_InsertsOnlyTheMissingGenres()
    {
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        // One genre already present locally; the sync should leave it and insert the other two.
        db.Genres.Add(Genre.Create(1, "Action"));
        await db.SaveChangesAsync();

        var tmdb = new StubTmdbClient
        {
            MovieGenres =
            [
                new TmdbGenre { TmdbGenreId = 1, Name = "Action" },
                new TmdbGenre { TmdbGenreId = 2, Name = "Drama" },
            ],
            SeriesGenres = [new TmdbGenre { TmdbGenreId = 3, Name = "Comedy" }],
        };
        var handler = new SyncGenresCommandHandler(db, tmdb, NullLogger<SyncGenresCommandHandler>.Instance);

        var result = await handler.Handle(new SyncGenresCommand(), CancellationToken.None);

        Assert.Equal(2, result.Inserted);       // 2 and 3 were missing
        Assert.Equal(1, result.AlreadyPresent); // 1 already existed
        Assert.Equal(3, result.Total);
        Assert.Equal(3, await db.Genres.CountAsync()); // no duplicates
    }

    [Fact]
    public async Task Handle_SaveRacesAndGenresPresentOnReload_ReturnsZeroInsertedAllAlreadyPresent()
    {
        using var store = new SharedSqliteStore();

        var tmdb = new StubTmdbClient
        {
            MovieGenres =
            [
                new TmdbGenre { TmdbGenreId = 1, Name = "Action" },
                new TmdbGenre { TmdbGenreId = 2, Name = "Drama" },
            ],
            SeriesGenres = [new TmdbGenre { TmdbGenreId = 3, Name = "Comedy" }],
        };

        // The genres a concurrent sync commits first — same TMDB ids, distinct internal Guids.
        var racedGenres = new[] { Genre.Create(1, "Action"), Genre.Create(2, "Drama"), Genre.Create(3, "Comedy") };

        await using var db = store.CreateContext();

        // On our insert save: a sibling commits all three genres, then the real SQLite save trips the unique
        // TmdbGenreId index rejecting our batch (which rolls back entirely, so this run commits nothing).
        db.RaceOnNextSave = () =>
        {
            using var concurrent = store.CreateContext();
            concurrent.Genres.AddRange(racedGenres);
            concurrent.SaveChanges();
        };

        var handler = new SyncGenresCommandHandler(db, tmdb, NullLogger<SyncGenresCommandHandler>.Instance);

        var result = await handler.Handle(new SyncGenresCommand(), CancellationToken.None);

        // The exception is reconciled, not surfaced: nothing was inserted this run and all three incoming
        // genres are now present (created by the racer).
        Assert.Equal(0, result.Inserted);
        Assert.Equal(3, result.AlreadyPresent);
        Assert.Equal(3, result.Total);
        Assert.Equal(3, await db.Genres.CountAsync()); // only the racer's three rows, no duplicates
    }
}
