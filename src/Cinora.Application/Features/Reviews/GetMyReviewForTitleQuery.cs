using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Reviews;

/// <summary>
/// Returns the current user's own review of a title (as a <see cref="ReviewVm"/> with <c>IsMine = true</c>),
/// or <c>null</c> when they have none or are anonymous. It drives the Details page's create-vs-edit decision:
/// <c>null</c> → show the write form; non-null → show the pinned own review with edit/delete affordances.
/// </summary>
/// <param name="MovieId">The internal <see cref="Movie.Id"/> whose review-by-me to fetch.</param>
public sealed record GetMyReviewForTitleQuery(Guid MovieId) : IRequest<ReviewVm?>;

/// <summary>Handles <see cref="GetMyReviewForTitleQuery"/>; anonymous callers short-circuit to <c>null</c>.</summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved current user (anonymous ⇒ no review).</param>
public sealed class GetMyReviewForTitleQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetMyReviewForTitleQuery, ReviewVm?>
{
    /// <inheritdoc />
    public async Task<ReviewVm?> Handle(GetMyReviewForTitleQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (currentUser.UserId is not { } me)
        {
            return null;
        }

        var row = await db.Reviews
            .AsNoTracking()
            .Where(review => review.MovieId == request.MovieId && review.UserId == me)
            .Select(ReviewProjection.ToRow(db, me))
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : ReviewProjection.ToVm(row, me);
    }
}
