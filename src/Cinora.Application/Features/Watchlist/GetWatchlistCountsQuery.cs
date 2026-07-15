using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Watchlist;

/// <summary>
/// Fetches the current user's per-status watchlist counts (Milestone 4.4, §6.1) — the numbers behind the
/// watchlist page's filter chips (Plan N · Watching N · Watched N · All N). The owner is resolved server-side
/// from <see cref="ICurrentUser"/> (ADR 0009); no user id is bound. Read-only (CQRS-pure).
/// </summary>
public sealed record GetWatchlistCountsQuery : IRequest<WatchlistCountsVm>;

/// <summary>The current user's watchlist counts, per status plus the total.</summary>
/// <param name="PlanToWatch">The number of titles the user plans to watch.</param>
/// <param name="Watching">The number of titles the user is currently watching.</param>
/// <param name="Watched">The number of titles the user has finished.</param>
/// <param name="Total">The total number of entries (the "All" chip).</param>
public sealed record WatchlistCountsVm(int PlanToWatch, int Watching, int Watched, int Total)
{
    /// <summary>An all-zero counts view (a user with no entries).</summary>
    public static readonly WatchlistCountsVm Empty = new(0, 0, 0, 0);

    /// <summary>The count for a given filter — a specific status, or the <see cref="Total"/> for "All" (<c>null</c>).</summary>
    /// <param name="status">The status to count, or <c>null</c> for the whole list.</param>
    /// <returns>The matching count.</returns>
    public int CountFor(WatchlistStatus? status) => status switch
    {
        WatchlistStatus.PlanToWatch => PlanToWatch,
        WatchlistStatus.Watching => Watching,
        WatchlistStatus.Watched => Watched,
        _ => Total,
    };
}

/// <summary>
/// Handles <see cref="GetWatchlistCountsQuery"/> with ONE grouped <c>AsNoTracking</c> read:
/// <c>Where(UserId == me).GroupBy(Status).Select(status, count)</c> — a single <c>GROUP BY</c>, not a query per
/// status. Index-backed by the <c>(UserId, Status, AddedAtUtc)</c> composite (its <c>(UserId, Status)</c>
/// prefix serves the grouping — §10, §12). The grouped rows are folded into the fixed four-number VM in memory.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved viewer (the page is <c>[Authorize]</c>, so never anonymous here).</param>
public sealed class GetWatchlistCountsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetWatchlistCountsQuery, WatchlistCountsVm>
{
    /// <inheritdoc />
    public async Task<WatchlistCountsVm> Handle(
        GetWatchlistCountsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();

        var grouped = await db.Watchlists
            .AsNoTracking()
            .Where(entry => entry.UserId == me)
            .GroupBy(entry => entry.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        int planToWatch = 0, watching = 0, watched = 0;
        foreach (var row in grouped)
        {
            switch (row.Status)
            {
                case WatchlistStatus.PlanToWatch:
                    planToWatch = row.Count;
                    break;
                case WatchlistStatus.Watching:
                    watching = row.Count;
                    break;
                case WatchlistStatus.Watched:
                    watched = row.Count;
                    break;
            }
        }

        return new WatchlistCountsVm(planToWatch, watching, watched, planToWatch + watching + watched);
    }
}
