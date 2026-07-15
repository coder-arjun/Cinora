using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Realtime;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinora.Application.Features.Reviews;

/// <summary>
/// Records that the current user likes a review (Milestone 3.2). Idempotent: the composite
/// <c>(ReviewId, UserId)</c> primary key guarantees at most one like per user per review, so a repeat like is
/// a no-op (§6.1). On a genuinely NEW like that is NOT a self-like — and only when no
/// <see cref="NotificationType.ReviewLiked"/> notification for this exact <c>(recipient, actor, review)</c>
/// already exists (the re-notify dedup obligation, so a like → unlike → like never spams a second one) — a
/// notification is co-persisted to the review's author in the SAME unit of work (§9.1). After that save commits,
/// a best-effort live push fires behind <c>IRealtimeNotifier</c> (§9.2 — a push failure never fails the like).
/// The acting user is server-resolved (ADR 0009).
/// </summary>
/// <param name="ReviewId">The id of the review to like (the resource; from the route).</param>
public sealed record LikeReviewCommand(Guid ReviewId) : IRequest<LikeToggleResult>;

/// <summary>The post-write like state of a review, used to re-render the like button.</summary>
/// <param name="LikeCount">The review's like count after the write.</param>
/// <param name="LikedByMe">Whether the current user now likes the review.</param>
public sealed record LikeToggleResult(int LikeCount, bool LikedByMe);

/// <summary>
/// Handles <see cref="LikeReviewCommand"/>: resolve the review's author (404 if the review is missing),
/// pre-probe the composite PK for an existing like (idempotent no-op), otherwise insert the like — and, when
/// the actor is not the author, co-persist the author's <c>ReviewLiked</c> notification in the same
/// <c>SaveChanges</c>. On a <see cref="DbUpdateException"/> the handler re-probes the composite PK: only a
/// concurrent duplicate that trips the <c>(ReviewId, UserId)</c> PK (the like now exists) is reconciled to a
/// no-op; any other fault (for instance the co-staged notification insert, or a wrapped transient) is
/// rethrown rather than masked as a successful like. The like count is recomputed from the store after the write.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (§2, ADR 0009).</param>
/// <param name="realtime">The realtime push port; a best-effort live push runs after a committed new notification (§9.2).</param>
/// <param name="pushDispatch">The Web Push fan-out port (ADR 0020); enqueued by the shared dispatcher after the SignalR push.</param>
/// <param name="logger">Records a Warning if the best-effort push fails (the like/notification still commit).</param>
public sealed class LikeReviewCommandHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    IRealtimeNotifier realtime,
    IPushDispatch pushDispatch,
    ILogger<LikeReviewCommandHandler> logger)
    : IRequestHandler<LikeReviewCommand, LikeToggleResult>
{
    /// <inheritdoc />
    public async Task<LikeToggleResult> Handle(LikeReviewCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.GetRequiredUserId();

        var authorId = await db.Reviews
            .AsNoTracking()
            .Where(review => review.Id == request.ReviewId)
            .Select(review => (Guid?)review.UserId)
            .FirstOrDefaultAsync(cancellationToken);

        if (authorId is null)
        {
            throw new NotFoundException($"Review ({request.ReviewId}) was not found.");
        }

        Notification? createdNotification = null;
        string? actorDisplayName = null;

        var alreadyLiked = await db.ReviewLikes
            .AsNoTracking()
            .AnyAsync(like => like.ReviewId == request.ReviewId && like.UserId == userId, cancellationToken);

        if (!alreadyLiked)
        {
            db.ReviewLikes.Add(ReviewLike.Create(request.ReviewId, userId));

            // Co-persist the author's notification for a genuinely new like — but never for a self-like (§9.1),
            // and never a DUPLICATE. A like → unlike → like must not spam a SECOND ReviewLiked notification, so
            // skip when a ReviewLiked notification for this exact (recipient, actor, review) already exists (the
            // 3.5 re-notify dedup obligation).
            if (authorId.Value != userId)
            {
                var alreadyNotified = await db.Notifications
                    .AsNoTracking()
                    .AnyAsync(
                        notification => notification.RecipientUserId == authorId.Value
                            && notification.ActorUserId == userId
                            && notification.TargetId == request.ReviewId
                            && notification.Type == NotificationType.ReviewLiked,
                        cancellationToken);

                if (!alreadyNotified)
                {
                    actorDisplayName = await db.Users
                        .AsNoTracking()
                        .Where(user => user.Id == userId)
                        .Select(user => user.DisplayName)
                        .FirstAsync(cancellationToken);

                    createdNotification = Notification.Create(
                        authorId.Value,
                        NotificationType.ReviewLiked,
                        $"{actorDisplayName} liked your review",
                        actorUserId: userId,
                        targetId: request.ReviewId);
                    db.Notifications.Add(createdNotification);
                }
            }

            var committed = false;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                committed = true;
            }
            catch (DbUpdateException)
            {
                // Discard our now-conflicting staged rows (the like AND its co-staged notification) so nothing
                // re-flushes, then re-probe to distinguish the EXPECTED race from a real fault. Only if the like
                // now exists was this the composite (ReviewId, UserId) PK race — reconcile it to the idempotent
                // no-op. Otherwise the failure was something else (e.g. the notification insert, or a wrapped
                // transient), which must NOT be masked as a successful like — rethrow it.
                db.DiscardPendingChanges();

                var racedLikeExists = await db.ReviewLikes
                    .AsNoTracking()
                    .AnyAsync(
                        like => like.ReviewId == request.ReviewId && like.UserId == userId,
                        cancellationToken);

                if (!racedLikeExists)
                {
                    throw;
                }

                // The discarded notification was never committed on this reconciled no-op path — no push.
                createdNotification = null;
            }

            // Best-effort live push AFTER the commit succeeds — a push failure never rolls back or fails the like
            // (N2, §9.2). Only push when a notification was actually created and committed (not a self-like, not
            // a deduped re-like, not the reconciled race).
            if (committed && createdNotification is not null)
            {
                var dto = new NotificationDto(
                    createdNotification.Id,
                    createdNotification.Type,
                    createdNotification.Message,
                    actorDisplayName,
                    createdNotification.TargetId,
                    createdNotification.CreatedAtUtc);

                await RealtimeNotificationDispatcher.DispatchBestEffortAsync(
                    realtime, db, pushDispatch, logger, authorId.Value, dto, cancellationToken);
            }
        }

        var likeCount = await db.ReviewLikes
            .AsNoTracking()
            .CountAsync(like => like.ReviewId == request.ReviewId, cancellationToken);

        return new LikeToggleResult(likeCount, LikedByMe: true);
    }
}
