namespace Cinora.Application.Common.Realtime;

/// <summary>
/// The realtime wire model for a conversation-membership or metadata change pushed over
/// <see cref="Interfaces.IChatNotifier"/> (§5). It signals affected members that their conversation list should
/// refresh (a group was created, a member added or removed, the group renamed, or someone left). An
/// Application-owned record — never a Domain entity on the wire.
/// </summary>
/// <param name="ConversationId">The conversation the event concerns.</param>
/// <param name="Kind">The event kind: <c>"created"</c>, <c>"member-added"</c>, <c>"member-removed"</c>,
/// <c>"renamed"</c>, or <c>"left"</c>.</param>
public sealed record ConversationEventDto(
    Guid ConversationId,
    string Kind);
