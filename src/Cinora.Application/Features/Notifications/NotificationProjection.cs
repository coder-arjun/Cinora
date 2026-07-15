using System.Linq.Expressions;
using Cinora.Application.Common.Interfaces;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;

namespace Cinora.Application.Features.Notifications;

/// <summary>
/// The one shared, translatable projection from a <see cref="Notification"/> to an inbox row. The actor's
/// display name AND avatar key are resolved by a SINGLE correlated sub-query against <c>Users</c> (both fields in
/// one read, mirroring the review/comment/friend projections); for a review-type notification the reviewed
/// title's TMDB coordinates are resolved by a single correlated sub-query that joins the target <c>Review</c> to
/// its <c>Movie</c> (yielding <c>null</c> for the friend-type notifications whose target is a <c>Friend</c> id,
/// not a review). One <c>AsNoTracking</c> query, no N+1 (§13).
/// </summary>
internal static class NotificationProjection
{
    /// <summary>Builds the EF projection expression for one notification, closing over the context for its sub-queries.</summary>
    /// <param name="db">The persistence context whose <c>Users</c>/<c>Reviews</c>/<c>Movies</c> sets back the correlated sub-queries.</param>
    /// <returns>An <see cref="Expression"/> projecting a <see cref="Notification"/> to a <see cref="NotificationRow"/>.</returns>
    public static Expression<Func<Notification, NotificationRow>> ToRow(IAppDbContext db) =>
        notification => new NotificationRow
        {
            Id = notification.Id,
            Type = notification.Type,
            Message = notification.Message,
            ActorUserId = notification.ActorUserId,
            Actor = db.Users
                .Where(user => user.Id == notification.ActorUserId)
                .Select(user => new NotificationActorRow
                {
                    DisplayName = user.DisplayName,
                    AvatarFileKey = user.AvatarFileKey,
                })
                .FirstOrDefault(),
            TargetId = notification.TargetId,
            CreatedAtUtc = notification.CreatedAtUtc,
            IsRead = notification.IsRead,

            // Resolve the reviewed title for a review-type target (its TargetId is a ReviewId). A friend-type
            // target's id is a Friend row id, so no Review matches and this stays null — the card falls back to a
            // profile / friends deep-link. One correlated sub-query joining Review → Movie (no N+1).
            ReviewTarget = (from review in db.Reviews
                            where review.Id == notification.TargetId
                            join movie in db.Movies on review.MovieId equals movie.Id
                            select new NotificationReviewTargetRow
                            {
                                TmdbId = movie.TmdbId,
                                MediaType = movie.MediaType,
                                Title = movie.Title,
                            }).FirstOrDefault(),
        };

    /// <summary>
    /// The lean, SINGLE-query projection for the Web Push fan-out (Milestone 6.4 §6.3, backlog 6.3 perf Low).
    /// It folds what the push handler previously took THREE round-trips to read — the enriched row, a 2nd
    /// point-read for the recipient id, and the recipient's per-type push preferences — into ONE
    /// <c>AsNoTracking</c> read, and unlike <see cref="ToRow"/> it does NOT fetch the actor's display name/avatar
    /// (push never renders them). It surfaces the recipient id, the review-target coordinates for the deep link,
    /// and the recipient's four push flags (a single correlated <c>Users</c> sub-query, null when no user row —
    /// which the handler treats as opt-in, matching the pre-fold behaviour).
    /// </summary>
    /// <param name="db">The persistence context whose <c>Reviews</c>/<c>Movies</c>/<c>Users</c> sets back the correlated sub-queries.</param>
    /// <returns>An <see cref="Expression"/> projecting a <see cref="Notification"/> to a <see cref="NotificationPushRow"/>.</returns>
    public static Expression<Func<Notification, NotificationPushRow>> ToPushRow(IAppDbContext db) =>
        notification => new NotificationPushRow
        {
            Type = notification.Type,
            Message = notification.Message,
            ActorUserId = notification.ActorUserId,
            RecipientUserId = notification.RecipientUserId,

            // Same review→movie join as ToRow (null for friend-type targets whose id is a Friend row id).
            ReviewTarget = (from review in db.Reviews
                            where review.Id == notification.TargetId
                            join movie in db.Movies on review.MovieId equals movie.Id
                            select new NotificationReviewTargetRow
                            {
                                TmdbId = movie.TmdbId,
                                MediaType = movie.MediaType,
                                Title = movie.Title,
                            }).FirstOrDefault(),

            // One correlated sub-query for the recipient's push flags (owned columns on Users). Null ⟺ no user
            // row → the handler defaults to opt-in. Mirrors the proven nested-init-projection shape of the actor
            // sub-query above so it translates identically on SQL Server and SQLite.
            RecipientPreferences = db.Users
                .Where(user => user.Id == notification.RecipientUserId)
                .Select(user => new NotificationRecipientPreferencesRow
                {
                    PushFriendRequests = user.Preferences.PushFriendRequests,
                    PushFriendAccepted = user.Preferences.PushFriendAccepted,
                    PushReviewLikes = user.Preferences.PushReviewLikes,
                    PushComments = user.Preferences.PushComments,
                })
                .FirstOrDefault(),
        };

    /// <summary>Maps a materialized <see cref="NotificationRow"/> to the public <see cref="NotificationVm"/> in memory.</summary>
    /// <param name="row">The materialized projection row.</param>
    /// <returns>The display view model.</returns>
    public static NotificationVm ToVm(NotificationRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new NotificationVm
        {
            Id = row.Id,
            Type = row.Type,
            Message = row.Message,
            ActorUserId = row.ActorUserId,
            ActorDisplayName = row.Actor?.DisplayName,
            ActorAvatarFileKey = row.Actor?.AvatarFileKey,
            TargetId = row.TargetId,
            CreatedAtUtc = row.CreatedAtUtc,
            IsRead = row.IsRead,
            ReviewTargetTmdbId = row.ReviewTarget?.TmdbId,
            ReviewTargetMedia = row.ReviewTarget?.MediaType,
            ReviewTargetTitle = row.ReviewTarget?.Title,
        };
    }
}

/// <summary>The intermediate shape EF materializes for a notification projection; <see cref="NotificationProjection.ToVm"/> completes the mapping.</summary>
internal sealed class NotificationRow
{
    /// <summary>The notification's unique identifier.</summary>
    public required Guid Id { get; init; }

    /// <summary>The kind of event.</summary>
    public required NotificationType Type { get; init; }

    /// <summary>The server-composed message.</summary>
    public required string Message { get; init; }

    /// <summary>The triggering user's id, or <c>null</c>.</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>The actor's display fields, resolved by one correlated <c>Users</c> sub-query (null when no actor).</summary>
    public NotificationActorRow? Actor { get; init; }

    /// <summary>The deep-link target id (a review id or a friend-request id), or <c>null</c>.</summary>
    public Guid? TargetId { get; init; }

    /// <summary>The UTC creation instant.</summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>Whether the recipient has read the notification.</summary>
    public required bool IsRead { get; init; }

    /// <summary>The reviewed title's coordinates for a review-type target, resolved by one correlated sub-query; <c>null</c> otherwise.</summary>
    public NotificationReviewTargetRow? ReviewTarget { get; init; }
}

/// <summary>The actor fields materialized by the single correlated <c>Users</c> sub-query in the notification projection.</summary>
internal sealed class NotificationActorRow
{
    /// <summary>The actor's public display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The actor's avatar storage key, or <c>null</c>.</summary>
    public string? AvatarFileKey { get; init; }
}

/// <summary>The reviewed-title fields materialized by the single correlated Review→Movie sub-query in the notification projection.</summary>
internal sealed class NotificationReviewTargetRow
{
    /// <summary>The reviewed title's TMDB identifier.</summary>
    public int TmdbId { get; init; }

    /// <summary>Whether the reviewed title is a movie or a series.</summary>
    public MediaType MediaType { get; init; }

    /// <summary>The reviewed title's display title.</summary>
    public string? Title { get; init; }
}

/// <summary>
/// The lean shape EF materializes for the Web Push fan-out (<see cref="NotificationProjection.ToPushRow"/>):
/// everything the dispatcher needs (recipient, type, message, deep-link coordinates, and the recipient's push
/// preferences) in ONE round-trip, without the actor display fields push never uses.
/// </summary>
internal sealed class NotificationPushRow
{
    /// <summary>The kind of event (drives the preference gate, the payload tag, and the deep link).</summary>
    public required NotificationType Type { get; init; }

    /// <summary>The server-composed message (the push body).</summary>
    public required string Message { get; init; }

    /// <summary>The triggering user's id (the FriendAccepted deep-link target), or <c>null</c>.</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>The recipient's id — who the devices and preferences belong to.</summary>
    public required Guid RecipientUserId { get; init; }

    /// <summary>The reviewed-title coordinates for a review-type target, or <c>null</c>.</summary>
    public NotificationReviewTargetRow? ReviewTarget { get; init; }

    /// <summary>The recipient's per-type push flags, or <c>null</c> when no user row exists (⇒ opt-in default).</summary>
    public NotificationRecipientPreferencesRow? RecipientPreferences { get; init; }
}

/// <summary>The recipient's per-type push flags, materialized by the single correlated <c>Users</c> sub-query in the push projection.</summary>
internal sealed class NotificationRecipientPreferencesRow
{
    /// <summary>Whether friend-request pushes are enabled.</summary>
    public bool PushFriendRequests { get; init; }

    /// <summary>Whether friend-accepted pushes are enabled.</summary>
    public bool PushFriendAccepted { get; init; }

    /// <summary>Whether review-like pushes are enabled.</summary>
    public bool PushReviewLikes { get; init; }

    /// <summary>Whether comment pushes are enabled.</summary>
    public bool PushComments { get; init; }
}
