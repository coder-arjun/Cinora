using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Realtime;
using Cinora.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Cinora.Web.Infrastructure;

/// <summary>
/// The self-hosted SignalR adapter for the Application <see cref="IRealtimeNotifier"/> port (ADR 0011 §3). It
/// lives in Web — the composition root — rather than Infrastructure, because the hub and
/// <see cref="INotificationClient"/> are hosting/transport types in this assembly and Infrastructure cannot
/// reference Web (the dependency rule); registering the adapter here keeps Infrastructure out of the realtime
/// transport entirely. It wraps the strongly-typed <see cref="IHubContext{THub, T}"/> and fans each push out to
/// the recipient's <c>user-{id}</c> group (every open tab/device). A send to a group with no live members is a
/// harmless no-op, so this is safe to call unconditionally after every notification is persisted.
/// </summary>
/// <param name="hub">The strongly-typed hub context used to invoke client methods on a group.</param>
public sealed class SignalRRealtimeNotifier(IHubContext<NotificationHub, INotificationClient> hub)
    : IRealtimeNotifier
{
    /// <inheritdoc />
    public Task NotifyAsync(Guid recipientUserId, NotificationDto notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // The recipient's Guid renders (invariant "D" form) to the SAME string as the hub's Context.UserIdentifier,
        // so the group targeted here matches the group the recipient's connections joined.
        return hub.Clients
            .Group(NotificationHub.UserGroup(recipientUserId.ToString()))
            .ReceiveNotification(notification);
    }

    /// <inheritdoc />
    public Task UnreadCountChangedAsync(Guid recipientUserId, int unreadCount, CancellationToken cancellationToken) =>
        hub.Clients
            .Group(NotificationHub.UserGroup(recipientUserId.ToString()))
            .UnreadCountChanged(unreadCount);
}
