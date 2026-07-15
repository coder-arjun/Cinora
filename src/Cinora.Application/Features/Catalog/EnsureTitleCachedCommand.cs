using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Tmdb;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinora.Application.Features.Catalog;

/// <summary>
/// Read-through, first-touch persistence of a single TMDB title into the local
/// <see cref="Movie"/>/<see cref="MovieGenre"/> tables. Idempotent by the unique <c>(TmdbId, MediaType)</c>
/// index: if the title is already cached it is a no-op returning the existing internal id; otherwise the
/// title is fetched from <see cref="ITmdbClient"/> and, unless TMDB has no such title (a 404), persisted via
/// <see cref="Movie.FromTmdb"/> with its genre links. This is the one canonical TMDB → internal-<see cref="Guid"/>
/// seam that later review and watchlist features reuse (ADR 0007).
/// </summary>
/// <param name="TmdbId">The TMDB identifier of the title to cache; must be positive.</param>
/// <param name="MediaType">Whether the title is a movie or a series.</param>
public sealed record EnsureTitleCachedCommand(int TmdbId, MediaType MediaType)
    : IRequest<EnsureTitleCachedResult>;

/// <summary>How an <see cref="EnsureTitleCachedCommand"/> resolved.</summary>
public enum EnsureTitleOutcome
{
    /// <summary>The title was fetched from TMDB and a new <see cref="Movie"/> row was created.</summary>
    Created = 0,

    /// <summary>The title was already cached locally; no row was created.</summary>
    AlreadyCached = 1,

    /// <summary>TMDB has no such title (a 404); no row was created.</summary>
    NotFound = 2,
}

/// <summary>The outcome of an <see cref="EnsureTitleCachedCommand"/>.</summary>
/// <param name="Outcome">Whether the title was created, already cached, or not found.</param>
/// <param name="MovieId">The internal <see cref="Movie.Id"/> when cached, or <c>null</c> when not found.</param>
public sealed record EnsureTitleCachedResult(EnsureTitleOutcome Outcome, Guid? MovieId)
{
    /// <summary>Creates a "created" result carrying the new internal id.</summary>
    /// <param name="movieId">The internal id of the newly created title.</param>
    /// <returns>A created result.</returns>
    public static EnsureTitleCachedResult Created(Guid movieId) => new(EnsureTitleOutcome.Created, movieId);

    /// <summary>Creates an "already cached" result carrying the existing internal id.</summary>
    /// <param name="movieId">The internal id of the already-cached title.</param>
    /// <returns>An already-cached result.</returns>
    public static EnsureTitleCachedResult AlreadyCached(Guid movieId) =>
        new(EnsureTitleOutcome.AlreadyCached, movieId);

    /// <summary>Creates a "not found" result (TMDB 404); no row was created.</summary>
    /// <returns>A not-found result.</returns>
    public static EnsureTitleCachedResult NotFound() => new(EnsureTitleOutcome.NotFound, null);
}

/// <summary>Validates <see cref="EnsureTitleCachedCommand"/>: the TMDB identifier must be positive.</summary>
public sealed class EnsureTitleCachedCommandValidator : AbstractValidator<EnsureTitleCachedCommand>
{
    /// <summary>Configures the rules for <see cref="EnsureTitleCachedCommand"/>.</summary>
    public EnsureTitleCachedCommandValidator()
    {
        RuleFor(command => command.TmdbId)
            .GreaterThan(0).WithMessage("TmdbId must be a positive TMDB identifier.");
    }
}

/// <summary>
/// Handles <see cref="EnsureTitleCachedCommand"/>. It first checks the local table directly (never the TMDB
/// cache); on a miss it fetches details from <see cref="ITmdbClient"/> and persists them in two independent
/// committed steps. Step one ensures every referenced <see cref="Genre"/> exists in its OWN
/// concurrency-safe <c>SaveChanges</c> (upserting any missing, tolerant of a genre-insert race); step two
/// writes the <see cref="Movie"/> and its <see cref="MovieGenre"/> links in a second <c>SaveChanges</c>.
/// Splitting the genre upsert out first — AND detaching any leftover <c>Added</c> genres on a race so they
/// cannot re-flush into step two (<see cref="IAppDbContext.DiscardPendingChanges"/>) — means a genre race
/// can never roll back or rethrow the title write.
/// A concurrent insert that trips the unique <c>(TmdbId, MediaType)</c> index on the title write is treated
/// as already-cached (the row is reloaded and returned) rather than surfaced as an error. A title with no
/// resolvable name (TMDB sent neither <c>title</c> nor <c>name</c>) is treated as not-found, not persisted.
/// </summary>
/// <param name="db">The catalog persistence context — direct table access, no repository.</param>
/// <param name="tmdb">The TMDB read port supplying title details.</param>
/// <param name="logger">The categorized logger; records a reconciled genre-insert race so a masked
/// non-race <see cref="DbUpdateException"/> stays observable.</param>
public sealed class EnsureTitleCachedCommandHandler(
    IAppDbContext db,
    ITmdbClient tmdb,
    ILogger<EnsureTitleCachedCommandHandler> logger)
    : IRequestHandler<EnsureTitleCachedCommand, EnsureTitleCachedResult>
{
    /// <inheritdoc />
    public async Task<EnsureTitleCachedResult> Handle(
        EnsureTitleCachedCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Idempotency fast path: one cheap indexed read straight from the table (NOT the TMDB cache).
        var existingId = await FindCachedIdAsync(request.TmdbId, request.MediaType, cancellationToken);
        if (existingId is not null)
        {
            return EnsureTitleCachedResult.AlreadyCached(existingId.Value);
        }

        // Cache miss: fetch details. A 404 (null details) is NOT persisted — return a not-found result. A
        // title with neither `title` nor `name` maps to a blank Title (TmdbMapper coalesces to string.Empty);
        // persisting it would trip Movie.FromTmdb's Guard.Required and surface as a 500, so treat it as
        // not-found too (nothing meaningful to cache).
        var details = await tmdb.GetDetailsAsync(request.MediaType, request.TmdbId, cancellationToken);
        if (details is null || string.IsNullOrWhiteSpace(details.Title))
        {
            return EnsureTitleCachedResult.NotFound();
        }

        // Step 1: ensure every referenced genre exists in its OWN committed, race-tolerant step BEFORE the
        // title write. A genre-insert conflict is reconciled here and can never roll back or rethrow step 2.
        var genreIds = await EnsureGenresAsync(details.Genres, cancellationToken);

        // Step 2: write the title and its genre links. Only this SaveChanges can race on the unique
        // (TmdbId, MediaType) index; the genres it links are already committed, so nothing here re-touches
        // the Genre table.
        var movie = Movie.FromTmdb(
            details.TmdbId,
            details.MediaType,
            details.Title,
            details.Overview,
            details.ReleaseDate?.ToDateTime(TimeOnly.MinValue),
            details.PosterPath,
            details.BackdropPath);

        db.Movies.Add(movie);
        LinkGenres(movie.Id, details.Genres, genreIds);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return EnsureTitleCachedResult.Created(movie.Id);
        }
        catch (DbUpdateException)
        {
            // A concurrent request may have inserted the same (TmdbId, MediaType) first, tripping the unique
            // index. If the row is now present, treat this as already-cached; otherwise the failure is real.
            var racedId = await FindCachedIdAsync(request.TmdbId, request.MediaType, cancellationToken);
            if (racedId is not null)
            {
                return EnsureTitleCachedResult.AlreadyCached(racedId.Value);
            }

            throw;
        }
    }

    // Direct, no-tracking existence probe against the unique (TmdbId, MediaType) index.
    private Task<Guid?> FindCachedIdAsync(int tmdbId, MediaType mediaType, CancellationToken cancellationToken) =>
        db.Movies
            .AsNoTracking()
            .Where(movie => movie.TmdbId == tmdbId && movie.MediaType == mediaType)
            .Select(movie => (Guid?)movie.Id)
            .FirstOrDefaultAsync(cancellationToken);

    // Step 1 (committed on its own): resolves every referenced TMDB genre id to a local Genre.Id, upserting
    // any that are missing in a SaveChanges that tolerates a concurrent genre-insert race, and returns the
    // full tmdbGenreId → internal-Guid map so step 2 can build the links without touching the Genre table.
    private async Task<Dictionary<int, Guid>> EnsureGenresAsync(
        IReadOnlyList<TmdbGenre> genres,
        CancellationToken cancellationToken)
    {
        if (genres.Count == 0)
        {
            return [];
        }

        var wantedIds = genres.Select(g => g.TmdbGenreId).Distinct().ToArray();
        var resolved = await LoadGenreIdsAsync(wantedIds, cancellationToken);

        var missing = genres
            .DistinctBy(g => g.TmdbGenreId)
            .Where(g => !resolved.ContainsKey(g.TmdbGenreId))
            .ToArray();

        // All genres already present (the common case — genres are normally pre-synced eagerly): no write.
        if (missing.Length == 0)
        {
            return resolved;
        }

        foreach (var tmdbGenre in missing)
        {
            var created = Genre.Create(tmdbGenre.TmdbGenreId, tmdbGenre.Name);
            db.Genres.Add(created);
            resolved[tmdbGenre.TmdbGenreId] = created.Id; // optimistic — authoritative on the success path
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return resolved;
        }
        catch (DbUpdateException exception)
        {
            // A concurrent sync inserted one or more of the same genres first, tripping the unique
            // TmdbGenreId index and rolling this SaveChanges (and our optimistic ids) back. That is tolerable
            // — the genres we need now exist (the racer created them). Log it (Warning) so a DbUpdateException
            // that is NOT actually a genre race stays observable rather than silently swallowed.
            CatalogLog.GenreInsertRaceReconciled(logger, exception, missing.Length);

            // Detach our now-conflicting Added genres BEFORE reloading. EF Core keeps them Added after the
            // failed save, and at THIS point the genres are the ONLY tracked work — the movie and its links
            // are added later, in step 2 — so clearing the tracker is safe and scoped. It is the crux of the
            // fix (H-1): without it those leftover Added genres re-flush into step 2's SaveChanges, trip the
            // unique index again, roll back the title write, and surface as a 500. With them detached, step 2
            // writes only the movie + links (which reference the already-committed genre ids reloaded below).
            db.DiscardPendingChanges();

            return await LoadGenreIdsAsync(wantedIds, cancellationToken);
        }
    }

    // Reads the internal ids of the genres with the given TMDB ids (projection → no entity tracking).
    private async Task<Dictionary<int, Guid>> LoadGenreIdsAsync(
        IReadOnlyList<int> wantedTmdbIds,
        CancellationToken cancellationToken) =>
        (await db.Genres
            .AsNoTracking()
            .Where(g => wantedTmdbIds.Contains(g.TmdbGenreId))
            .Select(g => new { g.TmdbGenreId, g.Id })
            .ToListAsync(cancellationToken))
        .ToDictionary(g => g.TmdbGenreId, g => g.Id);

    // Step 2 (no Genre writes): links the movie to each referenced genre using the ids resolved in step 1.
    private void LinkGenres(Guid movieId, IReadOnlyList<TmdbGenre> genres, Dictionary<int, Guid> genreIds)
    {
        foreach (var tmdbGenre in genres.DistinctBy(g => g.TmdbGenreId))
        {
            // EnsureGenresAsync guarantees every referenced genre resolves; guard defensively so a genre that
            // somehow stayed unresolved is skipped rather than tripping a foreign-key violation on the link.
            if (genreIds.TryGetValue(tmdbGenre.TmdbGenreId, out var genreId))
            {
                db.MovieGenres.Add(MovieGenre.Link(movieId, genreId));
            }
        }
    }
}
