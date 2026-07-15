using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Reviews;

/// <summary>
/// Projects a single review to a <see cref="ReviewVm"/> for rendering one review card — used by the write
/// controller to return the freshly created/edited card as an HTMX partial (and directly reusable by the
/// like/comment swaps in 3.2). Returns <c>null</c> when the review does not exist. Uses the same shared
/// <see cref="ReviewProjection"/> as the list, so a single card and a list card are always identical.
/// </summary>
/// <param name="ReviewId">The id of the review to project.</param>
public sealed record GetReviewCardQuery(Guid ReviewId) : IRequest<ReviewVm?>;

/// <summary>Handles <see cref="GetReviewCardQuery"/> with one <c>AsNoTracking</c> projection query.</summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The (possibly anonymous) viewer, for the per-viewer flags.</param>
public sealed class GetReviewCardQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetReviewCardQuery, ReviewVm?>
{
    /// <inheritdoc />
    public async Task<ReviewVm?> Handle(GetReviewCardQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.UserId;

        var row = await db.Reviews
            .AsNoTracking()
            .Where(review => review.Id == request.ReviewId)
            .Select(ReviewProjection.ToRow(db, me))
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : ReviewProjection.ToVm(row, me);
    }
}
