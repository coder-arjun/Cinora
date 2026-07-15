using Cinora.Application.Common.Tmdb;
using Cinora.Application.Features.Catalog;
using Cinora.Application.Tests.Common.Fakes;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinora.Application.Tests.Features.Catalog;

/// <summary>
/// Deterministic unit tests for <see cref="EnsureTitleCachedCommandHandler"/>'s non-happy paths — the
/// concurrent title-insert race (F5), the unnameable-title guard (F3) and the concurrent genre-insert race
/// that must NOT roll back the title write (H-1) — against an in-process SQLite <see cref="TestAppDbContext"/>.
/// SQLite is a real relational engine that enforces the unique indexes, so these races throw genuine
/// <see cref="DbUpdateException"/>s the shared-database integration suite cannot force deterministically,
/// without any real concurrency or I/O. The created / already-cached / not-found (404) / validation paths
/// remain covered end-to-end in <c>CatalogPersistenceTests</c>.
/// </summary>
public sealed class EnsureTitleCachedCommandHandlerTests
{
    // Handlers take a categorized logger; the race paths only log, so a null logger keeps the tests silent.
    private static EnsureTitleCachedCommandHandler CreateHandler(TestAppDbContext db, StubTmdbClient tmdb) =>
        new(db, tmdb, NullLogger<EnsureTitleCachedCommandHandler>.Instance);

    [Fact]
    public async Task Handle_SaveRacesButRowResolvesOnReload_ReturnsAlreadyCachedWithRacedId()
    {
        const int tmdbId = 555_001;
        using var store = new SharedSqliteStore();

        // The title a concurrent request commits first. Genres are empty, so the title write is the only
        // SaveChanges — making that single save the one the race fires on.
        var racedMovie = Movie.FromTmdb(tmdbId, MediaType.Movie, "Raced Title", null, null, null, null);

        var tmdb = new StubTmdbClient
        {
            Details = new TmdbTitleDetails
            {
                TmdbId = tmdbId,
                MediaType = MediaType.Movie,
                Title = "Fetched Title",
                Genres = [],
            },
        };

        await using var db = store.CreateContext();

        // On our title-write save: a sibling context commits the raced row into the shared store, then the
        // real SQLite save trips the unique (TmdbId, MediaType) index rejecting our insert. That save's
        // transaction rolls back, so our own Add is never committed and the handler's post-exception reload
        // sees only the raced row.
        db.RaceOnNextSave = () =>
        {
            using var concurrent = store.CreateContext();
            concurrent.Movies.Add(racedMovie);
            concurrent.SaveChanges();
        };

        var handler = CreateHandler(db, tmdb);

        var result = await handler.Handle(
            new EnsureTitleCachedCommand(tmdbId, MediaType.Movie),
            CancellationToken.None);

        // The DbUpdateException must NOT surface: the row is resolvable on reload, so the handler reports it
        // as already-cached and returns the RACED row's internal id (not our un-persisted one).
        Assert.Equal(EnsureTitleOutcome.AlreadyCached, result.Outcome);
        Assert.Equal(racedMovie.Id, result.MovieId);
    }

    [Fact]
    public async Task Handle_DetailsHaveBlankTitle_ReturnsNotFoundAndPersistsNothing()
    {
        const int tmdbId = 555_002;
        using var store = new SharedSqliteStore();

        // TMDB returned neither `title` nor `name`, so the mapper coalesced Title to blank. Persisting it
        // would trip Movie.FromTmdb's Guard.Required and surface as a 500; the handler must treat it as
        // not-found instead (F3).
        var tmdb = new StubTmdbClient
        {
            Details = new TmdbTitleDetails
            {
                TmdbId = tmdbId,
                MediaType = MediaType.Movie,
                Title = "   ",
                Genres = [],
            },
        };

        await using var db = store.CreateContext();
        var handler = CreateHandler(db, tmdb);

        var result = await handler.Handle(
            new EnsureTitleCachedCommand(tmdbId, MediaType.Movie),
            CancellationToken.None);

        Assert.Equal(EnsureTitleOutcome.NotFound, result.Outcome);
        Assert.Null(result.MovieId);
        Assert.Equal(0, await db.Movies.CountAsync()); // nothing was written
    }

    [Fact]
    public async Task Handle_GenreInsertRacesBeforeTitleWrite_PersistsTitleWithoutRethrowingOrDuplicating()
    {
        // H-1 regression. A genre referenced by the title is missing locally, so step 1 stages it and its
        // SaveChanges races a concurrent writer that commits the SAME genre first — tripping the unique
        // TmdbGenreId index (SQLite really enforces it). The bug: EF keeps the failed insert Added, so it
        // re-flushes into step 2's title SaveChanges, trips the index AGAIN, rolls back the title write, and
        // (the row not being resolvable) rethrows → a 500. The fix detaches that leftover Added genre before
        // step 2, so the title write commits. This test FAILS (throws DbUpdateException) against the pre-fix
        // handler and PASSES (Created) after it.
        const int tmdbId = 555_003;
        const int racedGenreId = 878; // e.g. TMDB "Science Fiction"
        using var store = new SharedSqliteStore();

        var tmdb = new StubTmdbClient
        {
            Details = new TmdbTitleDetails
            {
                TmdbId = tmdbId,
                MediaType = MediaType.Movie,
                Title = "Fetched Title",
                Genres = [new TmdbGenre { TmdbGenreId = racedGenreId, Name = "Science Fiction" }],
            },
        };

        // The genre a concurrent sync commits first — same TMDB id as the one the title needs, distinct Guid.
        var racedGenre = Genre.Create(racedGenreId, "Science Fiction");

        await using var db = store.CreateContext();

        // Race step 1's genre save: a sibling commits the genre, then our real SQLite save trips the unique
        // TmdbGenreId index. One-shot — step 2's title save then runs for real, un-raced.
        db.RaceOnNextSave = () =>
        {
            using var concurrent = store.CreateContext();
            concurrent.Genres.Add(racedGenre);
            concurrent.SaveChanges();
        };

        var handler = CreateHandler(db, tmdb);

        // Must NOT throw: the genre race is reconciled and the title write proceeds independently.
        var result = await handler.Handle(
            new EnsureTitleCachedCommand(tmdbId, MediaType.Movie),
            CancellationToken.None);

        // The title was created (a fresh id — no title raced us; only the genre did).
        Assert.Equal(EnsureTitleOutcome.Created, result.Outcome);
        Assert.NotNull(result.MovieId);

        // Exactly ONE genre row for the raced id — the racer's; our leftover Added insert was discarded, not
        // re-flushed into (and rolled back with) the title write.
        Assert.Equal(1, await db.Genres.CountAsync(g => g.TmdbGenreId == racedGenreId));

        // The movie persisted with its genre link (pointing at the committed genre id).
        var movie = await db.Movies.AsNoTracking().SingleAsync(m => m.TmdbId == tmdbId);
        Assert.Equal(result.MovieId, movie.Id);
        Assert.Equal(racedGenre.Id, await db.MovieGenres.AsNoTracking()
            .Where(link => link.MovieId == movie.Id)
            .Select(link => link.GenreId)
            .SingleAsync());
    }
}
