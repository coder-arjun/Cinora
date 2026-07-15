using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Watchlist;

/// <summary>
/// Fetches one keyset page of the CURRENT user's watchlist (Milestone 4.4, §6.1) — their own tracked titles,
/// newest-added first, optionally scoped to a single <see cref="WatchlistStatus"/>. The owner is resolved
/// server-side from <see cref="ICurrentUser"/> (ADR 0009): no user id is bound, so a caller can only ever read
/// their own list. It is a keyset read — <c>Take + 1</c> to detect <c>HasMore</c> without a second count, an
/// opaque <see cref="WatchlistCursor"/> over <c>(AddedAtUtc DESC, Id DESC)</c>, never an <c>OFFSET</c> — backed
/// by the §10 composite indexes, mirroring <c>GetActivityFeedQuery</c>.
/// </summary>
/// <param name="Filter">The status to scope the page to, or <c>null</c> for the whole list ("All").</param>
/// <param name="Sort">The sort order (index-backed; <see cref="WatchlistSort.RecentlyAdded"/> is the default).</param>
/// <param name="Cursor">The keyset cursor of the previous page's last item, or <c>null</c> for the first page.</param>
/// <param name="Take">The desired page size (clamped to a sane maximum in the handler).</param>
public sealed record GetMyWatchlistQuery(
    WatchlistStatus? Filter,
    WatchlistSort Sort = WatchlistSort.RecentlyAdded,
    WatchlistCursor? Cursor = null,
    int Take = 24) : IRequest<WatchlistPageVm>;

/// <summary>
/// Handles <see cref="GetMyWatchlistQuery"/> with a single <c>AsNoTracking</c> query: the owner filter, the
/// optional status <c>Where</c> (applied BEFORE the keyset predicate so <c>HasMore</c>/cursor reflect the
/// filtered set), the keyset predicate, the newest-added-first order, <c>Take + 1</c>, and a projection that
/// joins <c>Movies</c> by primary key via ONE correlated sub-query (poster/title/year) — N+1-free, exactly the
/// shape the feed uses (§12). Read-only — no writes (CQRS-pure).
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved viewer whose watchlist this is (ADR 0009).</param>
public sealed class GetMyWatchlistQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetMyWatchlistQuery, WatchlistPageVm>
{
    /// <summary>The largest page the handler will serve regardless of the requested <c>Take</c>.</summary>
    public const int MaxTake = 60;

    /// <inheritdoc />
    public async Task<WatchlistPageVm> Handle(GetMyWatchlistQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();
        var take = Math.Clamp(request.Take, 1, MaxTake);

        // Owner-scoped: a user can only ever read their OWN entries (no bound user id — ADR 0009).
        var query = db.Watchlists
            .AsNoTracking()
            .Where(entry => entry.UserId == me);

        // The status filter is applied BEFORE the keyset predicate, so HasMore/cursor reflect the filtered set
        // (mirrors GetTitleReviewsQuery). null = the whole list ("All").
        if (request.Filter is { } status)
        {
            query = query.Where(entry => entry.Status == status);
        }

        if (request.Cursor is { } cursor)
        {
            // Strictly-older than the cursor under (AddedAtUtc DESC, Id DESC): an earlier timestamp, or the same
            // timestamp with a smaller id. The Id tie-break keeps rows that share an AddedAtUtc stable across the
            // page boundary (no overlap, no gaps), the same keyset shape the feed proves (ADR 0014, §12).
            query = query.Where(entry =>
                entry.AddedAtUtc < cursor.SortKey
                || (entry.AddedAtUtc == cursor.SortKey && entry.Id.CompareTo(cursor.Id) < 0));
        }

        // WatchlistSort.RecentlyAdded is currently the only (index-backed) sort; the OrderBy is written once
        // here. Additional sorts would branch on request.Sort (kept index-backed per §6.1).
        var rows = await query
            .OrderByDescending(entry => entry.AddedAtUtc)
            .ThenByDescending(entry => entry.Id)
            .Take(take + 1)
            .Select(entry => new WatchlistItemRow
            {
                EntryId = entry.Id,
                MovieId = entry.MovieId,
                Status = entry.Status,
                AddedAtUtc = entry.AddedAtUtc,
                UpdatedAtUtc = entry.UpdatedAtUtc,
                // ONE correlated sub-query joins the title by PK (required Restrict FK → always present). No N+1.
                Movie = db.Movies
                    .Where(movie => movie.Id == entry.MovieId)
                    .Select(movie => new WatchlistMovieRow
                    {
                        TmdbId = movie.TmdbId,
                        MediaType = movie.MediaType,
                        Title = movie.Title,
                        PosterPath = movie.PosterPath,
                        ReleaseDate = movie.ReleaseDate,
                    })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > take;
        var items = rows.Take(take).Select(ToVm).ToList();

        // NextCursor = the last KEPT item's (AddedAtUtc, EntryId); opaque, never a page number/OFFSET.
        var nextCursor = hasMore && items.Count > 0
            ? new WatchlistCursor(items[^1].AddedAtUtc, items[^1].EntryId)
            : null;

        return new WatchlistPageVm(items, nextCursor, hasMore);
    }

    // Maps a materialized row to the public VM in memory: collapses the release date to a year and defends
    // against a (contractually impossible) missing title, mirroring FeedItemProjection.ToVm.
    private static WatchlistItemVm ToVm(WatchlistItemRow row) =>
        new()
        {
            EntryId = row.EntryId,
            MovieId = row.MovieId,
            TmdbId = row.Movie?.TmdbId ?? 0,
            Media = row.Movie?.MediaType ?? MediaType.Movie,
            Title = row.Movie?.Title ?? string.Empty,
            PosterPath = row.Movie?.PosterPath,
            ReleaseYear = row.Movie?.ReleaseDate?.Year,
            Status = row.Status,
            AddedAtUtc = row.AddedAtUtc,
            UpdatedAtUtc = row.UpdatedAtUtc,
        };
}

/// <summary>The intermediate shape EF materializes for a watchlist-page row; the join is completed client-side.</summary>
internal sealed class WatchlistItemRow
{
    /// <summary>The <c>Watchlist</c> entry id (the keyset tie-breaker).</summary>
    public required Guid EntryId { get; init; }

    /// <summary>The internal <c>Movie.Id</c> of the tracked title.</summary>
    public required Guid MovieId { get; init; }

    /// <summary>The tracked title's fields, resolved by ONE correlated sub-query against <c>Movies</c> by PK.</summary>
    public WatchlistMovieRow? Movie { get; init; }

    /// <summary>The current user's viewing status.</summary>
    public required WatchlistStatus Status { get; init; }

    /// <summary>The UTC instant the entry was added (the keyset sort key).</summary>
    public required DateTime AddedAtUtc { get; init; }

    /// <summary>The UTC instant the status last changed, or <c>null</c>.</summary>
    public DateTime? UpdatedAtUtc { get; init; }
}

/// <summary>The title fields materialized by the single correlated <c>Movies</c> sub-query in the page projection.</summary>
internal sealed class WatchlistMovieRow
{
    /// <summary>The title's TMDB identifier.</summary>
    public int TmdbId { get; init; }

    /// <summary>Whether the title is a movie or a series.</summary>
    public MediaType MediaType { get; init; }

    /// <summary>The title's display title.</summary>
    public string? Title { get; init; }

    /// <summary>The title's raw TMDB poster path, or <c>null</c>.</summary>
    public string? PosterPath { get; init; }

    /// <summary>The title's release date, or <c>null</c> (collapsed to a year in the VM).</summary>
    public DateTime? ReleaseDate { get; init; }
}
