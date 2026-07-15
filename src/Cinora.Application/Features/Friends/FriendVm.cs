namespace Cinora.Application.Features.Friends;

/// <summary>
/// One of the current user's accepted friends, projected for display: the "other" user's id (drives the
/// profile link and the remove action), their display name, and their optional avatar key. Carries only
/// presentation-ready primitives — no Domain entity, no EF type.
/// </summary>
/// <param name="UserId">The friend's user id (the "other" party in the accepted relationship).</param>
/// <param name="DisplayName">The friend's public display name.</param>
/// <param name="AvatarFileKey">The friend's avatar storage key, or <c>null</c> when none is set.</param>
public sealed record FriendVm(Guid UserId, string DisplayName, string? AvatarFileKey);

/// <summary>The current user's accepted friends (both request directions), newest display-name-ordered.</summary>
/// <param name="Friends">The accepted friends; empty when the user has none.</param>
public sealed record FriendsVm(IReadOnlyList<FriendVm> Friends);

/// <summary>
/// A pending friend request involving the current user, projected for display. <see cref="IsIncoming"/>
/// distinguishes a request the current user RECEIVED (they may accept/decline it, addressed by
/// <see cref="RequestId"/>) from one they SENT (shown as "pending"). The "other" user's display fields are
/// resolved at the data read.
/// </summary>
/// <param name="RequestId">The <c>Friend</c> row id — the resource the accept/decline actions target.</param>
/// <param name="OtherUserId">The other party's user id (the requester on an incoming request, the addressee on an outgoing one).</param>
/// <param name="OtherDisplayName">The other party's public display name.</param>
/// <param name="OtherAvatarFileKey">The other party's avatar storage key, or <c>null</c>.</param>
/// <param name="IsIncoming"><c>true</c> when the current user received the request; <c>false</c> when they sent it.</param>
public sealed record PendingRequestVm(
    Guid RequestId,
    Guid OtherUserId,
    string OtherDisplayName,
    string? OtherAvatarFileKey,
    bool IsIncoming);

/// <summary>The current user's pending friend requests, split into incoming (to answer) and outgoing (awaiting).</summary>
/// <param name="Incoming">Requests addressed to the current user, newest-first.</param>
/// <param name="Outgoing">Requests the current user sent, newest-first.</param>
public sealed record PendingRequestsVm(
    IReadOnlyList<PendingRequestVm> Incoming,
    IReadOnlyList<PendingRequestVm> Outgoing);
