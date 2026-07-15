using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Reviews;

/// <summary>
/// Deletes the current user's review. Ownership is enforced in the handler (403 for another user's review,
/// 404 for a missing one — §2). Deleting the review cascades to its <see cref="ReviewLike"/>s and
/// <see cref="Comment"/>s at the database level (both configured <c>OnDelete(Cascade)</c> on their review
/// foreign key), so the handler never deletes children by hand.
/// </summary>
/// <param name="ReviewId">The id of the review to delete (the resource; from the route).</param>
public sealed record DeleteReviewCommand(Guid ReviewId) : IRequest<Unit>;

/// <summary>
/// Handles <see cref="DeleteReviewCommand"/>: load the tracked review (404 if missing), enforce ownership
/// (403 if not the author), remove it and save — the FK cascade removes its likes and comments.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (§2, ADR 0009).</param>
public sealed class DeleteReviewCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<DeleteReviewCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(DeleteReviewCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.GetRequiredUserId();

        var review = await db.Reviews
            .FirstOrDefaultAsync(candidate => candidate.Id == request.ReviewId, cancellationToken)
            ?? throw new NotFoundException($"Review ({request.ReviewId}) was not found.");

        if (review.UserId != userId)
        {
            throw new ForbiddenAccessException("You can only delete your own review.");
        }

        db.Reviews.Remove(review);
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}
