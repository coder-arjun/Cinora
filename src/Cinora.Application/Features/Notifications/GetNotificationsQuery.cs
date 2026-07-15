using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Notifications;

/// <summary>
/// Fetches one keyset page of the CURRENT user's notification inbox, newest-first (§9.3). The recipient is
/// always the current user — a notification can only ever be read by its owner, so ownership is implicit in the
/// filter (there is no cross-user read path). Ordering and paging are keyset
/// <c>(CreatedAtUtc DESC, Id DESC)</c> with an opaque <see cref="NotificationCursor"/> — never an <c>OFFSET</c>.
/// The page also carries the recipient's total unread count for the header + badge seed.
/// </summary>
/// <param name="Cursor">The keyset cursor of the previous page's last item, or <c>null</c> for the first page.</param>
/// <param name="Take">The desired page size (clamped to a sane maximum).</param>
public sealed record GetNotificationsQuery(NotificationCursor? Cursor, int Take = 20) : IRequest<NotificationListVm>;

/// <summary>
/// Handles <see cref="GetNotificationsQuery"/> with a single <c>AsNoTracking</c> query: the recipient filter,
/// the keyset predicate, the newest-first order, <c>Take + 1</c> (to detect <c>HasMore</c> without a second
/// count), and the shared <see cref="NotificationProjection"/> (actor + review target as correlated sub-queries
/// — no N+1, §13), plus one bounded count for the unread total. Read-only (CQRS-pure).
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved recipient whose inbox this is (§2, ADR 0009).</param>
public sealed class GetNotificationsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetNotificationsQuery, NotificationListVm>
{
    /// <summary>The largest page the handler will serve regardless of the requested <c>Take</c>.</summary>
    public const int MaxTake = 50;

    /// <inheritdoc />
    public async Task<NotificationListVm> Handle(GetNotificationsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();
        var take = Math.Clamp(request.Take, 1, MaxTake);

        var query = db.Notifications
            .AsNoTracking()
            .Where(notification => notification.RecipientUserId == me);

        if (request.Cursor is { } cursor)
        {
            // Strictly-older than the cursor under (CreatedAtUtc DESC, Id DESC): an earlier timestamp, or the
            // same timestamp with a smaller id. The Id tie-break keeps rows that share a CreatedAtUtc stable
            // across the page boundary (no overlap, no gaps — even if a new notification lands mid-scroll).
            query = query.Where(notification =>
                notification.CreatedAtUtc < cursor.CreatedAtUtc
                || (notification.CreatedAtUtc == cursor.CreatedAtUtc && notification.Id.CompareTo(cursor.Id) < 0));
        }

        var rows = await query
            .OrderByDescending(notification => notification.CreatedAtUtc)
            .ThenByDescending(notification => notification.Id)
            .Take(take + 1)
            .Select(NotificationProjection.ToRow(db))
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > take;
        var items = rows.Take(take).Select(NotificationProjection.ToVm).ToList();

        var nextCursor = hasMore && items.Count > 0
            ? new NotificationCursor(items[^1].CreatedAtUtc, items[^1].Id)
            : null;

        var unreadCount = await db.Notifications
            .CountAsync(
                notification => notification.RecipientUserId == me && !notification.IsRead,
                cancellationToken);

        return new NotificationListVm(items, nextCursor, hasMore, unreadCount);
    }
}
