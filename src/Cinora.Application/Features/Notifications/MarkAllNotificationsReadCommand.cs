using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Notifications;

/// <summary>
/// Marks ALL of the current user's unread notifications as read in one action (§9.3) — the inbox "mark all as
/// read" affordance. Recipient-scoped (only the current user's rows), so there is no cross-user mutation. The
/// acting user is server-resolved (ADR 0009).
/// </summary>
public sealed record MarkAllNotificationsReadCommand : IRequest<int>;

/// <summary>
/// Handles <see cref="MarkAllNotificationsReadCommand"/>: load the current user's unread notifications (tracked),
/// mark each read via the domain method, and save once. Returns the number marked (0 saves nothing). The unread
/// set is bounded in practice; the load-mutate-save shape matches the codebase's write convention (domain method
/// on a tracked entity), not a bulk SQL update.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved recipient (§2, ADR 0009).</param>
public sealed class MarkAllNotificationsReadCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<MarkAllNotificationsReadCommand, int>
{
    /// <inheritdoc />
    public async Task<int> Handle(MarkAllNotificationsReadCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();

        var unread = await db.Notifications
            .Where(notification => notification.RecipientUserId == me && !notification.IsRead)
            .ToListAsync(cancellationToken);

        if (unread.Count == 0)
        {
            return 0;
        }

        foreach (var notification in unread)
        {
            notification.MarkRead();
        }

        await db.SaveChangesAsync(cancellationToken);
        return unread.Count;
    }
}
