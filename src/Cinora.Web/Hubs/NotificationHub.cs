using Cinora.Application.Common.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Cinora.Web.Hubs;

/// <summary>
/// The strongly-typed client surface a connected browser exposes to the server (ADR 0011 §2). Strong typing
/// means a renamed method breaks the build rather than silently failing at runtime. The JS client
/// (<c>@microsoft/signalr</c>, bundled locally) registers handlers for these two methods.
/// </summary>
public interface INotificationClient
{
    /// <summary>Delivers a newly created notification to the client (drives an on-brand toast + bell prepend).</summary>
    /// <param name="notification">The wire DTO to render.</param>
    /// <returns>A task representing the client-side invocation.</returns>
    Task ReceiveNotification(NotificationDto notification);

    /// <summary>Delivers the recipient's recomputed unread count so the live badge updates without a reload.</summary>
    /// <param name="unreadCount">The recipient's current unread notification count.</param>
    /// <returns>A task representing the client-side invocation.</returns>
    Task UnreadCountChanged(int unreadCount);
}

/// <summary>
/// The self-hosted SignalR hub for live in-app notifications (ADR 0011). It is a THIN dispatcher — it does no
/// database work; server-side events flow in through <c>IRealtimeNotifier</c> and out to per-user groups. It is
/// <see cref="AuthorizeAttribute"/> (fail-closed): an anonymous socket must never join a group, and without auth
/// <c>Context.UserIdentifier</c> would be null and every group would collapse to <c>user-</c> (N3). On connect
/// the connection is added to the <c>user-{UserIdentifier}</c> group, where <c>Context.UserIdentifier</c> is the
/// Identity cookie's <c>NameIdentifier</c> — the SAME <c>Guid</c> <c>ICurrentUser</c> resolves (ADR 0009), so a
/// server push addressed by a recipient's id reaches exactly that user's open tabs.
/// </summary>
[Authorize]
public sealed class NotificationHub : Hub<INotificationClient>
{
    /// <summary>The SignalR group name a user's connections are fanned out to.</summary>
    /// <param name="userIdentifier">The user's <c>NameIdentifier</c> / id (as the group-key string).</param>
    /// <returns>The <c>user-{id}</c> group name; the SAME string the realtime notifier targets, so they cannot drift.</returns>
    public static string UserGroup(string userIdentifier) => $"user-{userIdentifier}";

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        // Defense-in-depth (Phase-3 exit security review): [Authorize] guarantees an authenticated principal,
        // and the Identity cookie always issues a NameIdentifier — so this is unreachable today. But if a future
        // auth scheme ever admitted an authenticated principal WITHOUT a NameIdentifier, joining
        // UserGroup(null → "") would collapse every such socket into one shared "user-" group and cross-deliver
        // notifications between users. Reject rather than degrade: a connection with no stable id never joins.
        if (Context.UserIdentifier is null)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(Context.UserIdentifier));
        await base.OnConnectedAsync();
    }
}
