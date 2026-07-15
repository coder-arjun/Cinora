using Cinora.Application.Common.Realtime;

namespace Cinora.Application.Common.Interfaces;

/// <summary>
/// The Application port for pushing best-effort realtime chat signals (§4). It mirrors
/// <see cref="IRealtimeNotifier"/>: the persisted rows (<c>Message</c>, <c>ConversationMember.LastReadAtUtc</c>)
/// are always the source of truth — a thread is correct even if a socket is down — and this port is a live
/// accelerator over them. The adapter is the self-hosted SignalR <c>SignalRChatNotifier</c> in the Web layer
/// (the composition root), so Infrastructure stays out of the realtime transport and the dependency rule holds.
/// Every push is invoked <em>after</em> the co-persisting <c>SaveChangesAsync</c> succeeds and is wrapped
/// best-effort by the adapter — a push failure NEVER rolls back or fails the write.
/// </summary>
public interface IChatNotifier
{
    /// <summary>
    /// Pushes a newly sent message to the conversation's <b>current</b> members, addressed by their per-user
    /// group (resolved live from the membership rows at send time). Fanning out per current-member — rather than
    /// to a persistent per-conversation group a connection joined once — guarantees a user who has since been
    /// removed or left is never a recipient: the realtime layer enforces the same participation cut-off the
    /// persisted reads do, with no stale-socket leak (ADR 0024).
    /// </summary>
    /// <param name="conversationId">The conversation the message was sent to (for the client-side thread filter).</param>
    /// <param name="memberUserIds">The conversation's current member ids — the recipient set.</param>
    /// <param name="message">The wire DTO to deliver (never a Domain entity).</param>
    /// <param name="ct">A token to observe for cancellation.</param>
    /// <returns>A task that completes when the push has been dispatched.</returns>
    Task MessageSentAsync(
        Guid conversationId, IReadOnlyList<Guid> memberUserIds, ChatMessageDto message, CancellationToken ct);

    /// <summary>
    /// Pushes a reader's advanced read position to the conversation, so other participants' sent bubbles can
    /// flip to "seen".
    /// </summary>
    /// <param name="conversationId">The conversation whose read position advanced.</param>
    /// <param name="readerUserId">The user who read.</param>
    /// <param name="lastReadAtUtc">The UTC instant the reader has now read up to.</param>
    /// <param name="ct">A token to observe for cancellation.</param>
    /// <returns>A task that completes when the push has been dispatched.</returns>
    Task ConversationReadAsync(Guid conversationId, Guid readerUserId, DateTime lastReadAtUtc, CancellationToken ct);

    /// <summary>
    /// Pushes a conversation membership/metadata change to a specific set of affected members (used when the
    /// per-conversation group cannot be relied on — e.g. a just-added member is not yet joined, or a removed
    /// member must be told), so their conversation list refreshes.
    /// </summary>
    /// <param name="memberUserIds">The users to notify (each via their per-user group).</param>
    /// <param name="evt">The wire DTO describing the change (never a Domain entity).</param>
    /// <param name="ct">A token to observe for cancellation.</param>
    /// <returns>A task that completes when the push has been dispatched.</returns>
    Task ConversationChangedAsync(IReadOnlyList<Guid> memberUserIds, ConversationEventDto evt, CancellationToken ct);
}
