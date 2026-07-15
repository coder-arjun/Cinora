using Cinora.Domain.Enums;

namespace Cinora.Domain.ValueObjects;

/// <summary>
/// A user's per-type Web Push opt-out preferences (Milestone 6.3, ADR 0020 §4 / un-defers ADR 0015). A pure,
/// immutable value object owned by <see cref="Entities.User"/> — four booleans, one per <see cref="NotificationType"/>,
/// each <b>default <see langword="true"/></b> (opt-out semantics: a subscriber receives push for every type until
/// they mute one). It carries <b>no persistence concern</b>; the EF owned-type mapping lives in Infrastructure.
///
/// Scope is deliberately <b>push only</b>: the in-app inbox row and the SignalR live toast are always delivered
/// (the inbox is the authoritative source of truth, ADR 0011); only the out-of-app push fan-out consults
/// <see cref="IsPushEnabled(NotificationType)"/>. A future email / in-app-mute channel would migrate to a
/// per-<c>(UserId, Channel, Type)</c> entity (recorded in ADR 0020, not built).
/// </summary>
/// <param name="PushFriendRequests">Whether a <see cref="NotificationType.FriendRequest"/> event pushes.</param>
/// <param name="PushFriendAccepted">Whether a <see cref="NotificationType.FriendAccepted"/> event pushes.</param>
/// <param name="PushReviewLikes">Whether a <see cref="NotificationType.ReviewLiked"/> event pushes.</param>
/// <param name="PushComments">Whether a <see cref="NotificationType.CommentAdded"/> event pushes.</param>
public sealed record NotificationPreferences(
    bool PushFriendRequests,
    bool PushFriendAccepted,
    bool PushReviewLikes,
    bool PushComments)
{
    /// <summary>
    /// The all-enabled default (every push type on). Returns a fresh instance on each access so a persisted
    /// <see cref="Entities.User"/> never shares one owned-value instance with another tracked owner (an EF
    /// owned-type constraint). Value-equal to any other all-true instance.
    /// </summary>
    public static NotificationPreferences Default =>
        new(PushFriendRequests: true, PushFriendAccepted: true, PushReviewLikes: true, PushComments: true);

    /// <summary>Whether push is enabled for a given notification type.</summary>
    /// <param name="type">The notification event type.</param>
    /// <returns>The matching per-type flag; an unknown/future type defaults to <see langword="true"/> (opt-out semantics).</returns>
    public bool IsPushEnabled(NotificationType type) => type switch
    {
        NotificationType.FriendRequest => PushFriendRequests,
        NotificationType.FriendAccepted => PushFriendAccepted,
        NotificationType.ReviewLiked => PushReviewLikes,
        NotificationType.CommentAdded => PushComments,
        _ => true,
    };

    /// <summary>Returns a new value with the flag for one notification type set, leaving the others unchanged.</summary>
    /// <param name="type">The notification event type whose flag to change.</param>
    /// <param name="enabled">The new value for that type's push flag.</param>
    /// <returns>A new <see cref="NotificationPreferences"/> (this instance is never mutated).</returns>
    public NotificationPreferences With(NotificationType type, bool enabled) => type switch
    {
        NotificationType.FriendRequest => this with { PushFriendRequests = enabled },
        NotificationType.FriendAccepted => this with { PushFriendAccepted = enabled },
        NotificationType.ReviewLiked => this with { PushReviewLikes = enabled },
        NotificationType.CommentAdded => this with { PushComments = enabled },
        _ => this,
    };
}
