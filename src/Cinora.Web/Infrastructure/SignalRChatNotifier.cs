using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Realtime;
using Cinora.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Cinora.Web.Infrastructure;

/// <summary>
/// The self-hosted SignalR adapter for the Application <see cref="IChatNotifier"/> port (§4; mirrors
/// <see cref="SignalRRealtimeNotifier"/>, ADR 0011). It lives in Web — the composition root — because it wraps
/// the hub and <see cref="IChatClient"/> (transport types in this assembly) and Infrastructure cannot reference
/// Web (the dependency rule). Message and read pushes fan out to the <c>conversation-{id}</c> group (whoever has
/// the thread open); membership/metadata changes fan out to each affected member's <c>chat-user-{id}</c> group.
/// Every push is wrapped best-effort: it swallows all exceptions except <see cref="OperationCanceledException"/>
/// and logs a Warning, so a realtime failure NEVER rolls back or fails the already-committed write (the persisted
/// rows are authoritative).
/// </summary>
/// <param name="hub">The strongly-typed hub context used to invoke client methods on a group.</param>
/// <param name="pushDispatch">Enqueues the out-of-app Web Push (mobile notification) for a new message, off the request path.</param>
/// <param name="logger">Records a Warning when a best-effort push fails.</param>
public sealed partial class SignalRChatNotifier(
    IHubContext<ChatHub, IChatClient> hub,
    IPushDispatch pushDispatch,
    ILogger<SignalRChatNotifier> logger) : IChatNotifier
{
    /// <inheritdoc />
    public async Task MessageSentAsync(
        Guid conversationId, IReadOnlyList<Guid> memberUserIds, ChatMessageDto message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(memberUserIds);
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            // Fan out to each CURRENT member's per-user group (resolved live at send). A removed/left user is not
            // in this set, so their still-open socket never receives the message — the socket is fail-closed too.
            var groups = memberUserIds.Select(id => ChatHub.UserGroup(id.ToString())).ToList();
            await hub.Clients.Groups(groups).ReceiveMessage(message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPushFailed(logger, ex, conversationId);
        }

        // Out-of-app Web Push to the recipients' devices (the mobile notification-panel alert, like WhatsApp),
        // off the request path and best-effort — the recipient set (members except the sender) + device lookup are
        // resolved in the dispatch's own scope keyed by the message id.
        pushDispatch.EnqueueChatMessage(message.Id);
    }

    /// <inheritdoc />
    public async Task ConversationReadAsync(
        Guid conversationId, Guid readerUserId, DateTime lastReadAtUtc, CancellationToken ct)
    {
        try
        {
            await hub.Clients
                .Group(ChatHub.ConversationGroup(conversationId))
                .ConversationRead(conversationId, readerUserId, lastReadAtUtc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPushFailed(logger, ex, conversationId);
        }
    }

    /// <inheritdoc />
    public async Task ConversationChangedAsync(
        IReadOnlyList<Guid> memberUserIds, ConversationEventDto evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(memberUserIds);
        ArgumentNullException.ThrowIfNull(evt);

        try
        {
            var groups = memberUserIds.Select(id => ChatHub.UserGroup(id.ToString())).ToList();
            await hub.Clients.Groups(groups).ConversationUpdated(evt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPushFailed(logger, ex, evt.ConversationId);
        }
    }

    [LoggerMessage(
        EventId = 3600,
        Level = LogLevel.Warning,
        Message = "Best-effort chat realtime push for conversation {ConversationId} failed; the persisted " +
                  "message/read/membership change is already committed and remains authoritative.")]
    private static partial void LogPushFailed(ILogger logger, Exception exception, Guid conversationId);
}
