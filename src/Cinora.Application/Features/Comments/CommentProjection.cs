using System.Linq.Expressions;
using Cinora.Application.Common.Interfaces;
using Cinora.Domain.Entities;

namespace Cinora.Application.Features.Comments;

/// <summary>
/// The one shared, translatable projection from a <see cref="Comment"/> to a display row, reused by the
/// comment-thread list and the add-comment result so the shape cannot drift (DRY). The author's display
/// name/avatar are resolved by a SINGLE correlated sub-query against <c>Users</c> (both fields in one read);
/// <c>IsMine</c> is computed client-side in <see cref="ToVm"/> from the viewer. One <c>AsNoTracking</c> query,
/// no N+1 (§13).
/// </summary>
internal static class CommentProjection
{
    /// <summary>Builds the EF projection expression for one comment, closing over the context for the author sub-query.</summary>
    /// <param name="db">The persistence context whose <c>Users</c> set backs the correlated author sub-query.</param>
    /// <returns>An <see cref="Expression"/> projecting a <see cref="Comment"/> to a <see cref="CommentRow"/>.</returns>
    public static Expression<Func<Comment, CommentRow>> ToRow(IAppDbContext db) =>
        comment => new CommentRow
        {
            CommentId = comment.Id,
            ReviewId = comment.ReviewId,
            AuthorUserId = comment.UserId,
            Author = db.Users
                .Where(user => user.Id == comment.UserId)
                .Select(user => new CommentAuthorRow
                {
                    DisplayName = user.DisplayName,
                    AvatarFileKey = user.AvatarFileKey,
                })
                .FirstOrDefault(),
            Body = comment.Body,
            CreatedAtUtc = comment.CreatedAtUtc,
        };

    /// <summary>Maps a materialized <see cref="CommentRow"/> to the public <see cref="CommentVm"/> in memory.</summary>
    /// <param name="row">The materialized projection row.</param>
    /// <param name="currentUserId">The viewing user's id, or <c>null</c> when anonymous.</param>
    /// <returns>The display view model.</returns>
    public static CommentVm ToVm(CommentRow row, Guid? currentUserId)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new CommentVm
        {
            CommentId = row.CommentId,
            ReviewId = row.ReviewId,
            AuthorUserId = row.AuthorUserId,
            AuthorDisplayName = row.Author?.DisplayName ?? string.Empty,
            AuthorAvatarFileKey = row.Author?.AvatarFileKey,
            Body = row.Body,
            CreatedAtUtc = row.CreatedAtUtc,
            IsMine = currentUserId is { } me && row.AuthorUserId == me,
        };
    }
}

/// <summary>The intermediate shape EF materializes for a comment projection; <see cref="CommentProjection.ToVm"/> completes the mapping.</summary>
internal sealed class CommentRow
{
    /// <summary>The comment's unique identifier.</summary>
    public required Guid CommentId { get; init; }

    /// <summary>The id of the review the comment belongs to.</summary>
    public required Guid ReviewId { get; init; }

    /// <summary>The author's user id.</summary>
    public required Guid AuthorUserId { get; init; }

    /// <summary>The author fields, resolved by one correlated <c>Users</c> sub-query (never <c>null</c> in practice).</summary>
    public CommentAuthorRow? Author { get; init; }

    /// <summary>The plain-text comment body.</summary>
    public required string Body { get; init; }

    /// <summary>The UTC creation instant.</summary>
    public required DateTime CreatedAtUtc { get; init; }
}

/// <summary>The author fields materialized by the single correlated <c>Users</c> sub-query in <see cref="CommentProjection.ToRow"/>.</summary>
internal sealed class CommentAuthorRow
{
    /// <summary>The author's public display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The author's avatar storage key, or <c>null</c>.</summary>
    public string? AvatarFileKey { get; init; }
}
