using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Reviews;

/// <summary>
/// Fetches one keyset page of a title's reviews, newest-first, for the public Details reviews list (§5.1).
/// This is a PUBLIC read: the handler tolerates an anonymous <see cref="ICurrentUser"/> (the per-viewer
/// <c>LikedByMe</c>/<c>IsMine</c> flags simply come back <c>false</c>). Ordering and paging are keyset
/// <c>(CreatedAtUtc DESC, Id DESC)</c> with an opaque <see cref="ReviewCursor"/> — never an <c>OFFSET</c>.
/// </summary>
/// <param name="MovieId">The internal <see cref="Movie.Id"/> whose reviews to page.</param>
/// <param name="Cursor">The keyset cursor of the previous page's last item, or <c>null</c> for the first page.</param>
/// <param name="Take">The desired page size (clamped to a sane maximum).</param>
public sealed record GetTitleReviewsQuery(Guid MovieId, ReviewCursor? Cursor, int Take = 10)
    : IRequest<TitleReviewsVm>;

/// <summary>One keyset page of a title's reviews.</summary>
/// <param name="Items">The reviews on this page, newest-first.</param>
/// <param name="NextCursor">The cursor to fetch the next page, or <c>null</c> when there is no next page.</param>
/// <param name="HasMore">Whether a further page exists.</param>
/// <param name="MovieId">The internal title id these reviews belong to (echoed for the caller/partial).</param>
public sealed record TitleReviewsVm(
    IReadOnlyList<ReviewVm> Items,
    ReviewCursor? NextCursor,
    bool HasMore,
    Guid MovieId);

/// <summary>
/// Handles <see cref="GetTitleReviewsQuery"/> with a single <c>AsNoTracking</c> query: the keyset filter, the
/// newest-first order, <c>Take + 1</c> (to detect <c>HasMore</c> without a second count), and the shared
/// <see cref="ReviewProjection"/> (like/comment counts + <c>LikedByMe</c> as correlated sub-queries — no
/// N+1, §13).
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The (possibly anonymous) viewer, for the per-viewer flags.</param>
public sealed class GetTitleReviewsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetTitleReviewsQuery, TitleReviewsVm>
{
    /// <summary>The largest page the handler will serve regardless of the requested <c>Take</c>.</summary>
    public const int MaxTake = 50;

    /// <inheritdoc />
    public async Task<TitleReviewsVm> Handle(GetTitleReviewsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.UserId;
        var take = Math.Clamp(request.Take, 1, MaxTake);

        var query = db.Reviews
            .AsNoTracking()
            .Where(review => review.MovieId == request.MovieId);

        // Exclude the signed-in viewer's OWN review: it is surfaced separately in the pinned "your review"
        // region on the Details page, so listing it here too would duplicate its DOM id and break hx-target
        // (UI H1). Applied BEFORE the keyset/take so HasMore, the cursor, and the counts all reflect the
        // filtered set. Anonymous viewers (no id) still see every review, unchanged.
        if (me is { } meId)
        {
            query = query.Where(review => review.UserId != meId);
        }

        if (request.Cursor is { } cursor)
        {
            // Strictly-older than the cursor under (CreatedAtUtc DESC, Id DESC): an earlier timestamp, or the
            // same timestamp with a smaller id. The Id tie-break keeps rows that share a CreatedAtUtc stable
            // across the page boundary (no overlap, no gaps).
            query = query.Where(review =>
                review.CreatedAtUtc < cursor.CreatedAtUtc
                || (review.CreatedAtUtc == cursor.CreatedAtUtc && review.Id.CompareTo(cursor.Id) < 0));
        }

        var rows = await query
            .OrderByDescending(review => review.CreatedAtUtc)
            .ThenByDescending(review => review.Id)
            .Take(take + 1)
            .Select(ReviewProjection.ToRow(db, me))
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > take;
        var items = rows.Take(take).Select(row => ReviewProjection.ToVm(row, me)).ToList();

        var nextCursor = hasMore && items.Count > 0
            ? new ReviewCursor(items[^1].CreatedAtUtc, items[^1].ReviewId)
            : null;

        return new TitleReviewsVm(items, nextCursor, hasMore, request.MovieId);
    }
}
