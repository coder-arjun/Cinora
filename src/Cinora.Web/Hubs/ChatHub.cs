using System.Security.Claims;
using Cinora.Application.Common.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Cinora.Web.Hubs;

/// <summary>
/// The strongly-typed client surface a connected browser exposes to the chat server (§4). Strong typing means a
/// renamed method breaks the build rather than silently failing at runtime. The JS client
/// (<c>@microsoft/signalr</c>, bundled locally) registers handlers for these methods. All text carried here is
/// PLAIN TEXT — the client renders it via <c>textContent</c>, never <c>innerHTML</c> (§13).
/// </summary>
public interface IChatClient
{
    /// <summary>Delivers a newly sent message to the open thread (appended with an id-based dedupe).</summary>
    /// <param name="message">The message wire DTO.</param>
    /// <returns>A task representing the client-side invocation.</returns>
    Task ReceiveMessage(ChatMessageDto message);

    /// <summary>Signals that a participant is typing in a conversation (auto-clears client-side).</summary>
    /// <param name="conversationId">The conversation being typed in.</param>
    /// <param name="userId">The typing user's id.</param>
    /// <param name="displayName">The typing user's display name (PLAIN TEXT).</param>
    /// <returns>A task representing the client-side invocation.</returns>
    Task UserTyping(Guid conversationId, Guid userId, string displayName);

    /// <summary>Signals that a participant advanced their read position (flips sent bubbles to "seen").</summary>
    /// <param name="conversationId">The conversation whose read position advanced.</param>
    /// <param name="readerUserId">The user who read.</param>
    /// <param name="lastReadAtUtc">The instant they have now read up to.</param>
    /// <returns>A task representing the client-side invocation.</returns>
    Task ConversationRead(Guid conversationId, Guid readerUserId, DateTime lastReadAtUtc);

    /// <summary>Signals that a participant's device received the messages (drives the double "delivered" tick).</summary>
    /// <param name="conversationId">The conversation whose messages were delivered.</param>
    /// <param name="userId">The user whose device acknowledged delivery.</param>
    /// <returns>A task representing the client-side invocation.</returns>
    Task MessageDelivered(Guid conversationId, Guid userId);

    /// <summary>Signals a conversation membership/metadata change so the client refreshes its list.</summary>
    /// <param name="evt">The event wire DTO.</param>
    /// <returns>A task representing the client-side invocation.</returns>
    Task ConversationUpdated(ConversationEventDto evt);
}

/// <summary>
/// The self-hosted SignalR hub for live chat (§4; mirrors <see cref="NotificationHub"/>, ADR 0011). It is a THIN
/// dispatcher — the only database access is a membership check via the scoped <see cref="IChatMembership"/>;
/// server-side events flow in through <c>IChatNotifier</c> and out to per-conversation groups. It is
/// <see cref="AuthorizeAttribute"/> (fail-closed): an anonymous socket is rejected at negotiate, and a socket
/// with no stable <c>NameIdentifier</c> never joins any group. On connect the connection joins its per-user
/// <c>chat-user-{id}</c> group (for conversation-list updates); a thread is joined on demand via
/// <see cref="JoinConversation"/> after a membership check, so a non-participant can never receive a
/// conversation's messages (participation is the authorization, §3).
/// </summary>
/// <param name="membership">The scoped membership check (over a per-request <c>IAppDbContext</c>).</param>
[Authorize]
public sealed class ChatHub(IChatMembership membership) : Hub<IChatClient>
{
    /// <summary>The SignalR group name for a conversation's participants (those who have the thread open).</summary>
    /// <param name="conversationId">The conversation id.</param>
    /// <returns>The <c>conversation-{id}</c> group name; the SAME string the chat notifier targets.</returns>
    public static string ConversationGroup(Guid conversationId) => $"conversation-{conversationId}";

    /// <summary>The SignalR group name for a single user's connections (conversation-list fan-out).</summary>
    /// <param name="userId">The user's id (as the group-key string).</param>
    /// <returns>The <c>chat-user-{id}</c> group name; the SAME string the chat notifier targets.</returns>
    public static string UserGroup(string userId) => $"chat-user-{userId}";

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        // Fail-closed (mirrors NotificationHub): [Authorize] guarantees an authenticated principal and the
        // Identity cookie always issues a NameIdentifier, so this is unreachable today — but a connection with no
        // stable id must never join a "chat-user-" group that would collapse users together. Reject, don't degrade.
        if (Context.UserIdentifier is null)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(Context.UserIdentifier));
        await base.OnConnectedAsync();
    }

    /// <summary>Joins the caller's connection to a conversation's group — membership-checked (silent no-op if not a member).</summary>
    /// <param name="conversationId">The conversation to join.</param>
    public async Task JoinConversation(Guid conversationId)
    {
        var userId = RequireUserId();
        if (!await membership.IsMemberAsync(conversationId, userId, Context.ConnectionAborted))
        {
            return; // not a participant — never join the group (§3).
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroup(conversationId));
    }

    /// <summary>Removes the caller's connection from a conversation's group (on closing the thread).</summary>
    /// <param name="conversationId">The conversation to leave.</param>
    /// <returns>A task representing the group removal.</returns>
    public Task LeaveConversationGroup(Guid conversationId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, ConversationGroup(conversationId));

    /// <summary>Broadcasts a typing signal to the other participants of a conversation — membership-checked.</summary>
    /// <param name="conversationId">The conversation being typed in.</param>
    public async Task Typing(Guid conversationId)
    {
        var userId = RequireUserId();
        if (!await membership.IsMemberAsync(conversationId, userId, Context.ConnectionAborted))
        {
            return;
        }

        var name = Context.User?.FindFirst(ClaimTypes.Name)?.Value ?? "Someone";
        await Clients.OthersInGroup(ConversationGroup(conversationId)).UserTyping(conversationId, userId, name);
    }

    /// <summary>
    /// Acknowledges that the caller's device received a conversation's messages — membership-checked. Broadcasts
    /// <see cref="IChatClient.MessageDelivered"/> to the OTHER participants so a sender's tick can flip to the
    /// double "delivered" state.
    /// </summary>
    /// <param name="conversationId">The conversation whose messages were delivered to the caller.</param>
    public async Task MarkDelivered(Guid conversationId)
    {
        var userId = RequireUserId();
        if (!await membership.IsMemberAsync(conversationId, userId, Context.ConnectionAborted))
        {
            return;
        }

        await Clients.OthersInGroup(ConversationGroup(conversationId)).MessageDelivered(conversationId, userId);
    }

    // [Authorize] + the Identity cookie's NameIdentifier guarantee a parseable id (OnConnectedAsync rejects any
    // connection without one), so this never throws in practice.
    private Guid RequireUserId() => Guid.Parse(Context.UserIdentifier!);
}
