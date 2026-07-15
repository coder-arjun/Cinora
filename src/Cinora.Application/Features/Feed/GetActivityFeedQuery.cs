using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Friends;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Feed;

/// <summary>
/// Fetches one keyset page of the current user's activity feed — their accepted friends' recent reviews,
/// newest-first (ADR 0012, §8). This is a READ-TIME query: there is NO materialized feed table. The friend set
/// is resolved live (both request directions), then friends' reviews are keyset-paged over
/// <c>(CreatedAtUtc DESC, Id DESC)</c> with an opaque <see cref="FeedCursor"/> — never an <c>OFFSET</c>. The
/// current user's own reviews are excluded (the feed is "your friends"; self-activity lives on the profile).
/// Feed v1 is reviews-only (ADR 0012 §3).
/// </summary>
/// <param name="Cursor">The keyset cursor of the previous page's last item, or <c>null</c> for the first page.</param>
/// <param name="Take">The desired page size (clamped to a sane maximum).</param>
public sealed record GetActivityFeedQuery(FeedCursor? Cursor, int Take = 20) : IRequest<ActivityFeedVm>;

/// <summary>
/// Handles <see cref="GetActivityFeedQuery"/>. Resolves the current user's accepted-friend id set (an empty set
/// short-circuits to the "no friends" empty feed), then reads friends' reviews with a single
/// <c>AsNoTracking</c> query: the friend-set <c>IN</c> filter, the own-review exclusion, the keyset predicate,
/// the newest-first order, <c>Take + 1</c> (to detect <c>HasMore</c> without a second count), and the shared
/// <see cref="FeedItemProjection"/> (like/comment counts + author + title as correlated sub-queries — no N+1,
/// §13). Read-only — no writes (CQRS-pure).
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved viewer whose feed this is (§2, ADR 0009).</param>
public sealed class GetActivityFeedQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetActivityFeedQuery, ActivityFeedVm>
{
    /// <summary>The largest page the handler will serve regardless of the requested <c>Take</c>.</summary>
    public const int MaxTake = 50;

    /// <inheritdoc />
    public async Task<ActivityFeedVm> Handle(GetActivityFeedQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();
        var take = Math.Clamp(request.Take, 1, MaxTake);

        // 1. Resolve the accepted-friend id set (both directions). No friends → the "find friends" empty state.
        var friendIds = await FriendProjections.AcceptedFriendIdsAsync(db, me, cancellationToken);
        if (friendIds.Count == 0)
        {
            return new ActivityFeedVm([], NextCursor: null, HasMore: false, HasFriends: false);
        }

        // 2. Friends' reviews only, excluding my own. The IN (@friendIds) set is bounded in practice (§13);
        //    the ADR-0012 §4 trigger governs when a denormalized feed becomes warranted.
        var query = db.Reviews
            .AsNoTracking()
            .Where(review => review.UserId != me && friendIds.Contains(review.UserId));

        if (request.Cursor is { } cursor)
        {
            // Strictly-older than the cursor under (CreatedAtUtc DESC, Id DESC): an earlier timestamp, or the
            // same timestamp with a smaller id. The Id tie-break keeps rows that share a CreatedAtUtc stable
            // across the page boundary (no overlap, no gaps — even if a new review lands mid-scroll).
            query = query.Where(review =>
                review.CreatedAtUtc < cursor.CreatedAtUtc
                || (review.CreatedAtUtc == cursor.CreatedAtUtc && review.Id.CompareTo(cursor.Id) < 0));
        }

        var rows = await query
            .OrderByDescending(review => review.CreatedAtUtc)
            .ThenByDescending(review => review.Id)
            .Take(take + 1)
            .Select(FeedItemProjection.ToRow(db, me))
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > take;
        var items = rows.Take(take).Select(FeedItemProjection.ToVm).ToList();

        // 3. NextCursor = the last KEPT item's (CreatedAtUtc, Id); opaque, never a page number/OFFSET.
        var nextCursor = hasMore && items.Count > 0
            ? new FeedCursor(items[^1].CreatedAtUtc, items[^1].ReviewId)
            : null;

        return new ActivityFeedVm(items, nextCursor, hasMore, HasFriends: true);
    }
}
