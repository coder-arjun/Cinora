using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Reviews;

/// <summary>
/// Removes the current user's like of a review (Milestone 3.2). Idempotent: if the user has not liked the
/// review it is a no-op. Unliking never creates or removes a notification (the like's notification, if any,
/// stays — it recorded a real past event). The acting user is server-resolved (ADR 0009).
/// </summary>
/// <param name="ReviewId">The id of the review to unlike (the resource; from the route).</param>
public sealed record UnlikeReviewCommand(Guid ReviewId) : IRequest<LikeToggleResult>;

/// <summary>
/// Handles <see cref="UnlikeReviewCommand"/>: load the tracked <c>(ReviewId, currentUser)</c> like and, if
/// present, remove it and save; the like count is recomputed from the store after the write. A concurrent
/// unlike that deletes the same row first makes our delete affect 0 rows — the
/// <see cref="DbUpdateConcurrencyException"/> is caught and treated as the idempotent no-op it is (the row is
/// already gone).
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (§2, ADR 0009).</param>
public sealed class UnlikeReviewCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<UnlikeReviewCommand, LikeToggleResult>
{
    /// <inheritdoc />
    public async Task<LikeToggleResult> Handle(UnlikeReviewCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.GetRequiredUserId();

        var like = await db.ReviewLikes
            .FirstOrDefaultAsync(
                candidate => candidate.ReviewId == request.ReviewId && candidate.UserId == userId,
                cancellationToken);

        if (like is not null)
        {
            db.ReviewLikes.Remove(like);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A concurrent unlike (e.g. a fast double-click) already deleted the same (ReviewId, UserId)
                // row, so our DELETE affected 0 rows. That IS the desired end state — discard the stale tracked
                // delete and fall through to the recomputed count, returning the idempotent no-op.
                db.DiscardPendingChanges();
            }
        }

        var likeCount = await db.ReviewLikes
            .AsNoTracking()
            .CountAsync(candidate => candidate.ReviewId == request.ReviewId, cancellationToken);

        return new LikeToggleResult(likeCount, LikedByMe: false);
    }
}
