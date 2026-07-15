using Cinora.Domain.Enums;

namespace Cinora.Application.Features.Notifications;

/// <summary>
/// One notification projected for the inbox. Presentation-ready primitives only — no Domain entity and no EF
/// type — so it is safe to hand straight to a Razor partial. <see cref="Message"/> and
/// <see cref="ActorDisplayName"/> store the actor's RAW display name and are ALWAYS Razor output-encoded on
/// render (never <c>Html.Raw</c>; phase-3 XSS stance §3). <see cref="Type"/> + <see cref="TargetId"/> (and, for
/// review events, the resolved <see cref="ReviewTargetMedia"/>/<see cref="ReviewTargetTmdbId"/>) drive the
/// deep-link the item card renders.
/// </summary>
public sealed record NotificationVm
{
    /// <summary>The notification's unique identifier (also the keyset tie-breaker).</summary>
    public required Guid Id { get; init; }

    /// <summary>The kind of event (drives the icon and the deep-link resolution).</summary>
    public required NotificationType Type { get; init; }

    /// <summary>The short, server-composed notification text (already contains the actor's name).</summary>
    public required string Message { get; init; }

    /// <summary>The triggering user's id, or <c>null</c> for a system event (drives a profile deep-link).</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>The triggering user's display name, or <c>null</c> when there is no actor.</summary>
    public string? ActorDisplayName { get; init; }

    /// <summary>The triggering user's avatar storage key, or <c>null</c> (no actor, or the actor has no avatar).</summary>
    public string? ActorAvatarFileKey { get; init; }

    /// <summary>The related entity's id to deep-link to (a review id or a friend-request id), or <c>null</c>.</summary>
    public Guid? TargetId { get; init; }

    /// <summary>The UTC instant the notification was created (also the keyset sort key).</summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>Whether the recipient has read the notification.</summary>
    public required bool IsRead { get; init; }

    /// <summary>For a review event (<see cref="NotificationType.ReviewLiked"/>/<see cref="NotificationType.CommentAdded"/>),
    /// the reviewed title's TMDB id used to build the Details deep-link; <c>null</c> for non-review events.</summary>
    public int? ReviewTargetTmdbId { get; init; }

    /// <summary>For a review event, the reviewed title's media type (movie/series) for the Details deep-link segment.</summary>
    public MediaType? ReviewTargetMedia { get; init; }

    /// <summary>For a review event, the reviewed title's display title (a hint the card may show); <c>null</c> otherwise.</summary>
    public string? ReviewTargetTitle { get; init; }
}

/// <summary>
/// An opaque keyset cursor into the inbox, ordered newest-first by <c>(CreatedAtUtc DESC, Id DESC)</c>. A page
/// returns the cursor of its last item; the next request fetches strictly-older rows. Never a page number and
/// never an <c>OFFSET</c> (deep-pagination cost — §13).
/// </summary>
/// <param name="CreatedAtUtc">The <see cref="NotificationVm.CreatedAtUtc"/> of the last item on the current page.</param>
/// <param name="Id">The <see cref="NotificationVm.Id"/> of the last item on the current page (the tie-breaker).</param>
public sealed record NotificationCursor(DateTime CreatedAtUtc, Guid Id);

/// <summary>One keyset page of the current user's notification inbox.</summary>
/// <param name="Items">The notifications on this page, newest-first.</param>
/// <param name="NextCursor">The cursor to fetch the next page, or <c>null</c> when there is no next page.</param>
/// <param name="HasMore">Whether a further page exists.</param>
/// <param name="UnreadCount">The recipient's total unread count (drives the inbox header + the live badge seed).</param>
public sealed record NotificationListVm(
    IReadOnlyList<NotificationVm> Items,
    NotificationCursor? NextCursor,
    bool HasMore,
    int UnreadCount);
