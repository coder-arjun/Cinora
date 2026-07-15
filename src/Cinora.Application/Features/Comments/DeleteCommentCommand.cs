using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Comments;

/// <summary>
/// Deletes the current user's comment (Milestone 3.2). Ownership is enforced in the handler: only the comment's
/// author may delete it (a mismatch → 403; a missing comment → 404). Review-author moderation of others'
/// comments is deferred (§6). The acting user is server-resolved (ADR 0009).
/// </summary>
/// <param name="CommentId">The id of the comment to delete (the resource; from the route).</param>
public sealed record DeleteCommentCommand(Guid CommentId) : IRequest<Unit>;

/// <summary>
/// Handles <see cref="DeleteCommentCommand"/>: load the tracked comment (404 if missing), enforce authorship
/// (403 if not the author), remove it and save.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (§2, ADR 0009).</param>
public sealed class DeleteCommentCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<DeleteCommentCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(DeleteCommentCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.GetRequiredUserId();

        var comment = await db.Comments
            .FirstOrDefaultAsync(candidate => candidate.Id == request.CommentId, cancellationToken)
            ?? throw new NotFoundException($"Comment ({request.CommentId}) was not found.");

        if (comment.UserId != userId)
        {
            throw new ForbiddenAccessException("You can only delete your own comment.");
        }

        db.Comments.Remove(comment);
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}
