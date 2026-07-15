using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Tmdb;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinora.Application.Features.Catalog;

/// <summary>
/// Eagerly synchronises the local <see cref="Genre"/> table from TMDB's movie and TV genre lists. It is
/// idempotent: genres are upserted by their <see cref="Genre.TmdbGenreId"/> — missing ones are inserted and
/// already-present ones are left untouched — so it may run repeatedly (on demand or at startup) without
/// creating duplicates. Reads and writes go direct to the tables via <see cref="IAppDbContext"/>, never
/// through the TMDB cache (ADR 0007).
/// </summary>
public sealed record SyncGenresCommand : IRequest<SyncGenresResult>;

/// <summary>The outcome of a <see cref="SyncGenresCommand"/> run.</summary>
/// <param name="Inserted">The number of genres newly inserted this run.</param>
/// <param name="AlreadyPresent">The number of incoming genres that already existed and were left unchanged.</param>
public sealed record SyncGenresResult(int Inserted, int AlreadyPresent)
{
    /// <summary>The total number of distinct genres seen across TMDB's movie and TV lists.</summary>
    public int Total => Inserted + AlreadyPresent;
}

/// <summary>
/// Handles <see cref="SyncGenresCommand"/> by fetching the movie and series genre lists from
/// <see cref="ITmdbClient"/>, de-duplicating them by TMDB id (the two lists overlap), and inserting only the
/// genres not already present in the local table.
/// </summary>
/// <param name="db">The catalog persistence context — direct table access, no repository.</param>
/// <param name="tmdb">The TMDB read port supplying the genre lists.</param>
/// <param name="logger">The categorized logger; records a reconciled genre-insert race so a masked
/// non-race <see cref="DbUpdateException"/> stays observable.</param>
public sealed class SyncGenresCommandHandler(
    IAppDbContext db,
    ITmdbClient tmdb,
    ILogger<SyncGenresCommandHandler> logger)
    : IRequestHandler<SyncGenresCommand, SyncGenresResult>
{
    /// <inheritdoc />
    public async Task<SyncGenresResult> Handle(SyncGenresCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // TMDB splits genres into movie and TV lists whose numeric id-spaces overlap; fetch both and union.
        var movieGenres = await tmdb.GetGenresAsync(MediaType.Movie, cancellationToken);
        var seriesGenres = await tmdb.GetGenresAsync(MediaType.Series, cancellationToken);

        var incoming = movieGenres
            .Concat(seriesGenres)
            .GroupBy(genre => genre.TmdbGenreId)
            .Select(group => group.First())
            .ToArray();

        // One indexed read of the ids already present; the sync only ever inserts, never mutates existing rows.
        var known = (await db.Genres
                .AsNoTracking()
                .Select(genre => genre.TmdbGenreId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        // Partition the (already de-duplicated) incoming genres into the missing ones and the present ones.
        var toInsert = incoming.Where(genre => !known.Contains(genre.TmdbGenreId)).ToArray();
        var alreadyPresent = incoming.Length - toInsert.Length;

        // Skip the round-trip entirely when there is nothing new (the common steady-state case).
        if (toInsert.Length == 0)
        {
            return new SyncGenresResult(0, alreadyPresent);
        }

        foreach (var genre in toInsert)
        {
            db.Genres.Add(Genre.Create(genre.TmdbGenreId, genre.Name));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new SyncGenresResult(toInsert.Length, alreadyPresent);
        }
        catch (DbUpdateException exception)
        {
            // A concurrent sync inserted one or more of the SAME genres first, tripping the unique
            // TmdbGenreId index; SQL Server batches one transaction, so this whole SaveChanges rolled back
            // and THIS run committed nothing. That must not surface as an error (the genres now exist —
            // the racing sync created them). Log it (Warning) so a DbUpdateException that is NOT actually a
            // genre race stays observable, then reconcile the counts against the table instead. This mirrors
            // EnsureTitleCached's race path: catch → reload → return, never retry. We deliberately do NOT
            // re-attempt the insert: the change tracker still holds our now-conflicting Added genres, so a
            // second SaveChanges would trip the same index. Unlike EnsureTitleCached there is NO subsequent
            // save here — we reload and return, and the context is disposed with the request — so those
            // leftover Added genres are never flushed and need not be discarded. The sync is idempotent and
            // re-run (startup/on-demand), so any genre the racer did not create is inserted on the next run.
            CatalogLog.GenreInsertRaceReconciled(logger, exception, toInsert.Length);
            return await ReconcileAfterRaceAsync(incoming, cancellationToken);
        }
    }

    // Reloads the genre ids now present after a race and recomputes the outcome: THIS run inserted nothing
    // (the failed SaveChanges rolled back), and every incoming genre the racing sync created is reported as
    // AlreadyPresent rather than Inserted.
    private async Task<SyncGenresResult> ReconcileAfterRaceAsync(
        IReadOnlyList<TmdbGenre> incoming,
        CancellationToken cancellationToken)
    {
        var present = (await db.Genres
                .AsNoTracking()
                .Select(genre => genre.TmdbGenreId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var alreadyPresent = incoming.Count(genre => present.Contains(genre.TmdbGenreId));
        return new SyncGenresResult(0, alreadyPresent);
    }
}
