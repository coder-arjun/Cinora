using Cinora.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinora.Application.Common.Realtime;

/// <summary>
/// The single, shared best-effort realtime push used by every notification-producing handler (like, comment,
/// friend request, friend accept) so the fire-and-forget contract cannot drift across them (DRY, §9.2). It is
/// called <em>after</em> the co-persisting <c>SaveChangesAsync</c> has already committed the
/// <c>Notification</c> row: it pushes the DTO to the recipient, recomputes their unread count, and pushes that
/// for the live badge. The whole push is wrapped in a try/catch that swallows every exception except
/// <see cref="OperationCanceledException"/> and logs a Warning — a realtime failure must NEVER roll back or fail
/// the write, because the notification is already persisted and the inbox is authoritative (N2, ADR 0011).
/// </summary>
internal static partial class RealtimeNotificationDispatcher
{
    /// <summary>Pushes the notification and the recomputed unread count best-effort, then enqueues the Web Push fan-out; never throws (except on cancellation).</summary>
    /// <param name="realtime">The realtime push port (the SignalR adapter at runtime).</param>
    /// <param name="db">The persistence context, used to recompute the recipient's unread count.</param>
    /// <param name="pushDispatch">The Web Push fan-out port (ADR 0020) — enqueued off the critical path after the SignalR push.</param>
    /// <param name="logger">The producing handler's logger, used to record a Warning if a push fails.</param>
    /// <param name="recipientUserId">The user to notify (the notification's recipient).</param>
    /// <param name="notification">The wire DTO built from the just-committed notification row.</param>
    /// <param name="cancellationToken">A token to observe for cancellation (a cancellation is allowed to propagate).</param>
    /// <returns>A task that completes once the realtime push has been attempted and the Web Push fan-out enqueued.</returns>
    public static async Task DispatchBestEffortAsync(
        IRealtimeNotifier realtime,
        IAppDbContext db,
        IPushDispatch pushDispatch,
        ILogger logger,
        Guid recipientUserId,
        NotificationDto notification,
        CancellationToken cancellationToken)
    {
        try
        {
            await realtime.NotifyAsync(recipientUserId, notification, cancellationToken);

            // Recompute the recipient's unread count (the same shape the inbox / badge fallback use) so the live
            // badge updates without a reload. Uses the (RecipientUserId, IsRead) index.
            var unreadCount = await db.Notifications
                .CountAsync(
                    candidate => candidate.RecipientUserId == recipientUserId && !candidate.IsRead,
                    cancellationToken);

            await realtime.UnreadCountChangedAsync(recipientUserId, unreadCount, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The notification row is already committed; a dropped/failed push is a missed LIVE toast, never a
            // missed notification — the inbox still shows it. Log a Warning and carry on (N2, §9.2).
            LogPushFailed(logger, ex, recipientUserId);
        }

        // Web Push (ADR 0020 §3.4): out-of-app reach for the SAME event, enqueued OFF the request path via
        // IPushDispatch. Wrapped in its own try/catch and placed AFTER the SignalR block so (a) an enqueue fault
        // can never disturb the realtime path or fail the already-committed write, and (b) the push still fires
        // even when the SignalR push threw — a closed tab (SignalR delivered nothing) is exactly when push
        // matters most. Enqueue returns immediately; the actual send runs in the dispatcher's own scope.
        try
        {
            pushDispatch.Enqueue(notification.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPushEnqueueFailed(logger, ex, recipientUserId);
        }
    }

    [LoggerMessage(
        EventId = 3500,
        Level = LogLevel.Warning,
        Message = "Best-effort realtime notification push to recipient {RecipientUserId} failed; the notification " +
                  "row is already committed and remains visible in the recipient's inbox.")]
    private static partial void LogPushFailed(ILogger logger, Exception exception, Guid recipientUserId);

    [LoggerMessage(
        EventId = 3502,
        Level = LogLevel.Warning,
        Message = "Enqueuing the best-effort Web Push fan-out for recipient {RecipientUserId} failed; the " +
                  "notification is already committed and the in-app inbox / SignalR toast are unaffected.")]
    private static partial void LogPushEnqueueFailed(ILogger logger, Exception exception, Guid recipientUserId);
}
