using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Watchlist;

/// <summary>
/// The batch watchlist-status resolver for a whole rail / search page — the N+1 killer (ADR 0014). Given a
/// page of TMDB ids and their media type, it returns the current user's status for each title that is BOTH
/// first-touch persisted AND in their watchlist, as a <c>TmdbId → status</c> map. Titles not yet persisted
/// (most rail cards never earn a <c>Movie</c> row — ADR 0007) or simply not in the list are <b>absent</b> from
/// the map → the card renders "Add to watchlist". Exactly one <c>AsNoTracking</c> query, backed by the
/// existing <c>Movies (TmdbId, MediaType)</c> and <c>Watchlists (UserId, MovieId)</c> unique indexes — no new
/// index. Anonymous callers get an empty map without a database round-trip.
/// </summary>
/// <param name="TmdbIds">The page's TMDB ids (a single media type per page).</param>
/// <param name="Media">Whether the page is movies or series.</param>
public sealed record GetWatchlistStatusMapQuery(IReadOnlyList<int> TmdbIds, MediaType Media)
    : IRequest<IReadOnlyDictionary<int, WatchlistStatus>>;

/// <summary>
/// Handles <see cref="GetWatchlistStatusMapQuery"/> with ONE joined <c>AsNoTracking</c> read:
/// <c>Movies.Where(media &amp;&amp; TmdbIds.Contains).Join(Watchlists where UserId == me on Id == MovieId)</c>,
/// materialised to a dictionary. The controller composes this into the rail/search view model for
/// authenticated users only; the rail query itself stays <c>ITmdbClient</c>-only and cacheable.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved current user (anonymous → empty map).</param>
public sealed class GetWatchlistStatusMapQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetWatchlistStatusMapQuery, IReadOnlyDictionary<int, WatchlistStatus>>
{
    private static readonly IReadOnlyDictionary<int, WatchlistStatus> Empty =
        new Dictionary<int, WatchlistStatus>();

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, WatchlistStatus>> Handle(
        GetWatchlistStatusMapQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Anonymous, or an empty page, needs no query — the map is simply empty (every card shows "Add").
        if (currentUser.UserId is not { } userId || request.TmdbIds.Count == 0)
        {
            return Empty;
        }

        var tmdbIds = request.TmdbIds.Distinct().ToArray();

        // One SQL round-trip: the join of the page's persisted titles to THIS user's watchlist rows.
        var rows = await db.Movies
            .AsNoTracking()
            .Where(movie => movie.MediaType == request.Media && tmdbIds.Contains(movie.TmdbId))
            .Join(
                db.Watchlists.Where(entry => entry.UserId == userId),
                movie => movie.Id,
                entry => entry.MovieId,
                (movie, entry) => new { movie.TmdbId, entry.Status })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(row => row.TmdbId, row => row.Status);
    }
}
