using Cinora.Application.Common.Realtime;

namespace Cinora.Application.Common.Interfaces;

/// <summary>
/// The Application port for pushing a live, best-effort realtime signal to a specific user (ADR 0011). The
/// persisted <c>Notification</c> row is always the source of truth (the inbox is correct even if a socket is
/// down); this port is a live accelerator over it. The adapter is the self-hosted SignalR
/// <c>SignalRRealtimeNotifier</c> in the Web layer (the composition root), so Infrastructure stays out of the
/// realtime transport and the dependency rule holds. Push is invoked <em>after</em> the co-persisting
/// <c>SaveChangesAsync</c> succeeds and is wrapped best-effort by the caller — a push failure NEVER rolls back
/// or fails the write (§9.2).
/// </summary>
public interface IRealtimeNotifier
{
    /// <summary>
    /// Pushes a newly created notification to every open connection the recipient has (a group-per-user fan-out).
    /// </summary>
    /// <param name="recipientUserId">The user to notify — the same <c>Guid</c> <c>ICurrentUser</c> resolves.</param>
    /// <param name="notification">The wire DTO to deliver (never a Domain entity).</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>A task that completes when the push has been dispatched.</returns>
    Task NotifyAsync(Guid recipientUserId, NotificationDto notification, CancellationToken cancellationToken);

    /// <summary>
    /// Pushes the recipient's recomputed unread notification count so their live badge updates without a reload.
    /// </summary>
    /// <param name="recipientUserId">The user whose badge to update.</param>
    /// <param name="unreadCount">The recipient's current unread notification count.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>A task that completes when the count push has been dispatched.</returns>
    Task UnreadCountChangedAsync(Guid recipientUserId, int unreadCount, CancellationToken cancellationToken);
}
