using System.Linq.Expressions;
using Cinora.Application.Common.Interfaces;
using Cinora.Domain.Entities;
using Cinora.Domain.ValueObjects;

namespace Cinora.Application.Features.Reviews;

/// <summary>
/// The one shared, translatable projection from a <see cref="Review"/> to a display row, reused by the
/// title-reviews list, the single-card query, and the "my review" query so the shape cannot drift (DRY). The
/// aggregate <c>LikeCount</c>/<c>CommentCount</c> and the per-viewer <c>LikedByMe</c> are computed as
/// correlated sub-queries INSIDE the single SQL statement (no N+1 — §13); the author's display name/avatar are
/// resolved by a correlated scalar sub-query against the same context. The <c>Rating</c> value object is
/// materialized through its EF value converter here and unwrapped to its <see cref="int"/> in
/// <see cref="ToVm"/> (client-side) — reading <c>Rating.Value</c> in the SQL projection is not guaranteed
/// translatable, whereas selecting the converted property is.
/// </summary>
internal static class ReviewProjection
{
    /// <summary>
    /// Builds the EF projection expression for one review, closing over the context (for the correlated
    /// count/like/author sub-queries) and the current user id (for <c>LikedByMe</c>). An anonymous viewer
    /// (<paramref name="currentUserId"/> is <c>null</c>) yields <c>LikedByMe = false</c> naturally, because no
    /// like row's <c>UserId</c> equals a <c>null</c> parameter.
    /// </summary>
    /// <param name="db">The persistence context whose sets back the correlated sub-queries.</param>
    /// <param name="currentUserId">The viewing user's id, or <c>null</c> when anonymous.</param>
    /// <returns>An <see cref="Expression"/> projecting a <see cref="Review"/> to a <see cref="ReviewRow"/>.</returns>
    public static Expression<Func<Review, ReviewRow>> ToRow(IAppDbContext db, Guid? currentUserId) =>
        review => new ReviewRow
        {
            ReviewId = review.Id,
            AuthorUserId = review.UserId,
            Author = db.Users
                .Where(user => user.Id == review.UserId)
                .Select(user => new ReviewAuthorRow
                {
                    DisplayName = user.DisplayName,
                    AvatarFileKey = user.AvatarFileKey,
                })
                .FirstOrDefault(),
            Rating = review.Rating,
            Body = review.Body,
            CreatedAtUtc = review.CreatedAtUtc,
            UpdatedAtUtc = review.UpdatedAtUtc,
            LikeCount = db.ReviewLikes.Count(like => like.ReviewId == review.Id),
            CommentCount = db.Comments.Count(comment => comment.ReviewId == review.Id),
            LikedByMe = db.ReviewLikes.Any(like => like.ReviewId == review.Id && like.UserId == currentUserId),
        };

    /// <summary>
    /// Maps a materialized <see cref="ReviewRow"/> to the public <see cref="ReviewVm"/> in memory: unwraps the
    /// <see cref="Rating"/> value object to its <see cref="int"/> and computes <c>IsMine</c> from the viewer.
    /// </summary>
    /// <param name="row">The materialized projection row.</param>
    /// <param name="currentUserId">The viewing user's id, or <c>null</c> when anonymous.</param>
    /// <returns>The display view model.</returns>
    public static ReviewVm ToVm(ReviewRow row, Guid? currentUserId)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new ReviewVm
        {
            ReviewId = row.ReviewId,
            AuthorUserId = row.AuthorUserId,
            AuthorDisplayName = row.Author?.DisplayName ?? string.Empty,
            AuthorAvatarFileKey = row.Author?.AvatarFileKey,
            Rating = row.Rating.Value,
            Body = row.Body,
            CreatedAtUtc = row.CreatedAtUtc,
            UpdatedAtUtc = row.UpdatedAtUtc,
            LikeCount = row.LikeCount,
            CommentCount = row.CommentCount,
            LikedByMe = row.LikedByMe,
            IsMine = currentUserId is { } me && row.AuthorUserId == me,
        };
    }
}

/// <summary>
/// The intermediate shape EF materializes for a review projection: it holds the <see cref="Rating"/> value
/// object (materialized through its converter) rather than the unwrapped <see cref="int"/>, so the SQL
/// projection stays translatable; <see cref="ReviewProjection.ToVm"/> completes the mapping client-side.
/// </summary>
internal sealed class ReviewRow
{
    /// <summary>The review's unique identifier.</summary>
    public required Guid ReviewId { get; init; }

    /// <summary>The author's user id.</summary>
    public required Guid AuthorUserId { get; init; }

    /// <summary>
    /// The author's display name + avatar, resolved by ONE correlated sub-query against <c>Users</c>
    /// (never <c>null</c> in practice — a review's author always exists). Folding both fields into a single
    /// sub-query avoids two separate correlated reads of the same row.
    /// </summary>
    public ReviewAuthorRow? Author { get; init; }

    /// <summary>The rating value object (materialized through its EF value converter).</summary>
    public required Rating Rating { get; init; }

    /// <summary>The plain-text review body.</summary>
    public required string Body { get; init; }

    /// <summary>The UTC creation instant.</summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>The UTC last-edited instant, or <c>null</c>.</summary>
    public DateTime? UpdatedAtUtc { get; init; }

    /// <summary>The like count (a correlated sub-query).</summary>
    public required int LikeCount { get; init; }

    /// <summary>The comment count (a correlated sub-query).</summary>
    public required int CommentCount { get; init; }

    /// <summary>Whether the current viewer liked the review (a correlated <c>Any</c>).</summary>
    public required bool LikedByMe { get; init; }
}

/// <summary>
/// The author fields materialized by the single correlated <c>Users</c> sub-query in
/// <see cref="ReviewProjection.ToRow"/>.
/// </summary>
internal sealed class ReviewAuthorRow
{
    /// <summary>The author's public display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The author's avatar storage key, or <c>null</c>.</summary>
    public string? AvatarFileKey { get; init; }
}
