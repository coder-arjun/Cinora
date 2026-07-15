using System.Globalization;
using Cinora.Domain.Enums;

namespace Cinora.Application.Features.Notifications;

/// <summary>
/// The single source of the notification → route grammar (ADR 0020 §5). It was duplicated inline in
/// <c>_NotificationItem.cshtml</c>; consolidating it here means the inbox card's <c>href</c> and the Web Push
/// payload's <c>data.url</c> resolve <b>identically</b>. The grammar is deliberately kept as-is (parity over the
/// design sketch — no <c>#review-{id}</c> fragment): a review event (like/comment) with resolvable coordinates
/// deep-links to the reviewed title's Details page; a friend request links to <c>/friends</c>; a friend-accepted
/// links to the new friend's profile; anything unresolved yields <c>null</c> (the card renders without a link;
/// the push falls back to <c>"/"</c>). Every returned URL is a <b>same-origin relative</b> path.
/// </summary>
public static class NotificationDeepLink
{
    /// <summary>Resolves the deep-link target for a notification from its type and enriched coordinates.</summary>
    /// <param name="type">The notification's event type.</param>
    /// <param name="reviewTargetTmdbId">The reviewed title's TMDB id for a review-type target, else <c>null</c>.</param>
    /// <param name="reviewTargetMedia">The reviewed title's media type for a review-type target, else <c>null</c>.</param>
    /// <param name="actorUserId">The triggering user's id (used by the friend-accepted link), or <c>null</c>.</param>
    /// <returns>A same-origin relative URL, or <c>null</c> when no target can be resolved.</returns>
    public static string? Resolve(
        NotificationType type,
        int? reviewTargetTmdbId,
        MediaType? reviewTargetMedia,
        Guid? actorUserId) =>
        type switch
        {
            NotificationType.ReviewLiked or NotificationType.CommentAdded
                when reviewTargetTmdbId is int tmdbId && reviewTargetMedia is MediaType media
                => $"/discover/title/{media.ToString().ToLowerInvariant()}/{tmdbId.ToString(CultureInfo.InvariantCulture)}",
            NotificationType.FriendRequest => "/friends",
            NotificationType.FriendAccepted when actorUserId is Guid actorId => $"/users/{actorId}",
            _ => null,
        };
}
