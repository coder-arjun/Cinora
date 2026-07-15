using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Realtime;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinora.Application.Features.Comments;

/// <summary>
/// Adds the current user's comment to a review (Milestone 3.2). The acting user is server-resolved (ADR 0009);
/// the command carries only the target <paramref name="ReviewId"/> and the body. A missing review is a 404.
/// Unless the comment is on the user's own review, a <see cref="NotificationType.CommentAdded"/> notification is
/// co-persisted to the review's author in the same unit of work (§9.1 — the row only; the live push arrives in
/// 3.5). Comments are FLAT (no threading) and plain text (XSS stance §3).
/// </summary>
/// <param name="ReviewId">The id of the review being commented on (the resource; from the route).</param>
/// <param name="Body">The plain-text comment body (required, max <see cref="Comment.BodyMaxLength"/>).</param>
public sealed record AddCommentCommand(Guid ReviewId, string Body) : IRequest<AddCommentResult>;

/// <summary>The outcome of an <see cref="AddCommentCommand"/>: the new comment's id and its rendered card model.</summary>
/// <param name="CommentId">The id of the created comment.</param>
/// <param name="Card">The created comment projected for display (append to the thread).</param>
public sealed record AddCommentResult(Guid CommentId, CommentVm Card);

/// <summary>Validates <see cref="AddCommentCommand"/>: the body is present and within the domain length bound.</summary>
public sealed class AddCommentCommandValidator : AbstractValidator<AddCommentCommand>
{
    /// <summary>Configures the rules for <see cref="AddCommentCommand"/>.</summary>
    public AddCommentCommandValidator()
    {
        RuleFor(command => command.Body)
            .NotEmpty().WithMessage("A comment cannot be empty.")
            .MaximumLength(Comment.BodyMaxLength)
            .WithMessage($"A comment cannot exceed {Comment.BodyMaxLength} characters.");
    }
}

/// <summary>
/// Handles <see cref="AddCommentCommand"/>: resolve the review's author (404 if missing), persist the comment
/// (and, unless self-authored, the author's <c>CommentAdded</c> notification) in one <c>SaveChanges</c>, then
/// project the new comment to a <see cref="CommentVm"/> via the shared <see cref="CommentProjection"/>.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (§2, ADR 0009).</param>
/// <param name="realtime">The realtime push port; a best-effort live push runs after a committed notification (§9.2).</param>
/// <param name="pushDispatch">The Web Push fan-out port (ADR 0020); enqueued by the shared dispatcher after the SignalR push.</param>
/// <param name="logger">Records a Warning if the best-effort push fails (the comment/notification still commit).</param>
public sealed class AddCommentCommandHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    IRealtimeNotifier realtime,
    IPushDispatch pushDispatch,
    ILogger<AddCommentCommandHandler> logger)
    : IRequestHandler<AddCommentCommand, AddCommentResult>
{
    /// <inheritdoc />
    public async Task<AddCommentResult> Handle(AddCommentCommand request, CancellationToken cancellationToken)
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

        var comment = Comment.Create(request.ReviewId, userId, request.Body);
        db.Comments.Add(comment);

        Notification? createdNotification = null;
        string? actorDisplayName = null;
        if (authorId.Value != userId)
        {
            actorDisplayName = await db.Users
                .AsNoTracking()
                .Where(user => user.Id == userId)
                .Select(user => user.DisplayName)
                .FirstAsync(cancellationToken);

            createdNotification = Notification.Create(
                authorId.Value,
                NotificationType.CommentAdded,
                $"{actorDisplayName} commented on your review",
                actorUserId: userId,
                targetId: request.ReviewId);
            db.Notifications.Add(createdNotification);
        }

        await db.SaveChangesAsync(cancellationToken);

        // Best-effort live push AFTER the commit — a push failure never fails the comment (§9.2). Only when a
        // notification was actually created (not a self-comment).
        if (createdNotification is not null)
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

        var row = await db.Comments
            .AsNoTracking()
            .Where(candidate => candidate.Id == comment.Id)
            .Select(CommentProjection.ToRow(db))
            .FirstAsync(cancellationToken);

        return new AddCommentResult(comment.Id, CommentProjection.ToVm(row, userId));
    }
}
