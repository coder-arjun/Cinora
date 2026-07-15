using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Comments;

/// <summary>
/// Fetches one keyset page of a review's comment thread, OLDEST-FIRST, for the public thread on the Details
/// page (§6.1). A PUBLIC read: the handler tolerates an anonymous <see cref="ICurrentUser"/> (the per-viewer
/// <c>IsMine</c> flag comes back <c>false</c>). Ordering and paging are keyset <c>(CreatedAtUtc ASC, Id ASC)</c>
/// with an opaque <see cref="CommentCursor"/> over the existing <c>(ReviewId, CreatedAtUtc)</c> index — never
/// an <c>OFFSET</c>.
/// </summary>
/// <param name="ReviewId">The review whose comments to page.</param>
/// <param name="Cursor">The keyset cursor of the previous page's last item, or <c>null</c> for the first page.</param>
/// <param name="Take">The desired page size (clamped to a sane maximum).</param>
public sealed record GetReviewCommentsQuery(Guid ReviewId, CommentCursor? Cursor, int Take = 20)
    : IRequest<CommentListVm>;

/// <summary>One keyset page of a review's comments.</summary>
/// <param name="Items">The comments on this page, oldest-first.</param>
/// <param name="NextCursor">The cursor to fetch the next page, or <c>null</c> when there is no next page.</param>
/// <param name="HasMore">Whether a further page exists.</param>
/// <param name="ReviewId">The review these comments belong to (echoed for the caller/partial).</param>
public sealed record CommentListVm(
    IReadOnlyList<CommentVm> Items,
    CommentCursor? NextCursor,
    bool HasMore,
    Guid ReviewId);

/// <summary>
/// Handles <see cref="GetReviewCommentsQuery"/> with a single <c>AsNoTracking</c> query: the keyset filter, the
/// oldest-first order, <c>Take + 1</c> (to detect <c>HasMore</c>), and the shared <see cref="CommentProjection"/>
/// (author resolved by one correlated sub-query — no N+1, §13).
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The (possibly anonymous) viewer, for the <c>IsMine</c> flag.</param>
public sealed class GetReviewCommentsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetReviewCommentsQuery, CommentListVm>
{
    /// <summary>The largest page the handler will serve regardless of the requested <c>Take</c>.</summary>
    public const int MaxTake = 50;

    /// <inheritdoc />
    public async Task<CommentListVm> Handle(GetReviewCommentsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.UserId;
        var take = Math.Clamp(request.Take, 1, MaxTake);

        var query = db.Comments
            .AsNoTracking()
            .Where(comment => comment.ReviewId == request.ReviewId);

        if (request.Cursor is { } cursor)
        {
            // Strictly-newer than the cursor under (CreatedAtUtc ASC, Id ASC): a later timestamp, or the same
            // timestamp with a larger id. The Id tie-break keeps rows that share a CreatedAtUtc stable across
            // the page boundary (no overlap, no gaps).
            query = query.Where(comment =>
                comment.CreatedAtUtc > cursor.CreatedAtUtc
                || (comment.CreatedAtUtc == cursor.CreatedAtUtc && comment.Id.CompareTo(cursor.Id) > 0));
        }

        var rows = await query
            .OrderBy(comment => comment.CreatedAtUtc)
            .ThenBy(comment => comment.Id)
            .Take(take + 1)
            .Select(CommentProjection.ToRow(db))
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > take;
        var items = rows.Take(take).Select(row => CommentProjection.ToVm(row, me)).ToList();

        var nextCursor = hasMore && items.Count > 0
            ? new CommentCursor(items[^1].CreatedAtUtc, items[^1].CommentId)
            : null;

        return new CommentListVm(items, nextCursor, hasMore, request.ReviewId);
    }
}
