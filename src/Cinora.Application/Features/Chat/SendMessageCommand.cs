using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Realtime;
using Cinora.Application.Features.Blocks;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Chat;

/// <summary>
/// Sends a message to a conversation the current user is a member of (§3, §5). Membership is the authorization:
/// a non-member is forbidden, a missing conversation is a 404. For a 1:1 chat the block relationship is
/// re-checked at send time (a friendship/block can change after the conversation exists). The actor is
/// server-resolved (ADR 0009); after the co-persisting save the message is pushed best-effort over
/// <see cref="IChatNotifier"/>.
/// </summary>
/// <param name="ConversationId">The conversation to post to.</param>
/// <param name="Body">The message text; required, max <see cref="Message.BodyMaxLength"/> characters.</param>
public sealed record SendMessageCommand(Guid ConversationId, string Body) : IRequest<ChatMessageVm>;

/// <summary>Validates <see cref="SendMessageCommand"/>: a present conversation and a non-blank, bounded body.</summary>
public sealed class SendMessageCommandValidator : AbstractValidator<SendMessageCommand>
{
    /// <summary>Configures the rules for <see cref="SendMessageCommand"/>.</summary>
    public SendMessageCommandValidator()
    {
        RuleFor(command => command.ConversationId)
            .NotEmpty().WithMessage("A conversation is required.");

        RuleFor(command => command.Body)
            .NotEmpty().WithMessage("A message is required.")
            .MaximumLength(Message.BodyMaxLength)
                .WithMessage($"A message must be at most {Message.BodyMaxLength} characters.");
    }
}

/// <summary>
/// Handles <see cref="SendMessageCommand"/>. It loads the conversation's member ids (a missing set → 404, a
/// non-member sender → 403), re-checks the block gate for a 1:1 chat, then creates and persists the
/// <see cref="Message"/> and bumps the conversation's <c>LastMessageAtUtc</c> in one unit of work. After the
/// commit it pushes the message best-effort over <see cref="IChatNotifier"/> (a push failure never fails the
/// send) and returns the sender's own rendered <see cref="ChatMessageVm"/>.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting sender (ADR 0009).</param>
/// <param name="chatNotifier">The best-effort realtime push port (§4); the real adapter is wired in C3.</param>
public sealed class SendMessageCommandHandler(IAppDbContext db, ICurrentUser currentUser, IChatNotifier chatNotifier)
    : IRequestHandler<SendMessageCommand, ChatMessageVm>
{
    /// <inheritdoc />
    public async Task<ChatMessageVm> Handle(SendMessageCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();

        var members = await db.ConversationMembers.AsNoTracking()
            .Where(m => m.ConversationId == request.ConversationId)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
        if (members.Count == 0)
        {
            throw new NotFoundException($"Conversation ({request.ConversationId}) was not found.");
        }

        if (!members.Contains(me))
        {
            throw new ForbiddenAccessException("You are not a member of this conversation.");
        }

        var conversation = await db.Conversations
            .FirstAsync(c => c.Id == request.ConversationId, cancellationToken); // tracked, for the Type gate + bump

        // DM block re-check — ONLY for a true Direct conversation (a group that has shrunk to two members is
        // still a group, so gate on Type, not the member count).
        if (conversation.Type == ConversationType.Direct && members.Count == 2)
        {
            var other = members.First(u => u != me);
            if (await BlockQueries.AreBlockedEitherWayAsync(db, me, other, cancellationToken))
            {
                throw new ForbiddenAccessException("You cannot message this user.");
            }
        }

        // Domain guards length/blank → DomainException → 400 (defence in depth with the validator).
        var message = Message.Create(request.ConversationId, me, request.Body);
        db.Messages.Add(message);
        conversation.BumpLastMessage(message.SentAtUtc);

        await db.SaveChangesAsync(cancellationToken);

        var sender = await db.Users.AsNoTracking()
            .Where(u => u.Id == me)
            .Select(u => new { u.DisplayName, u.AvatarFileKey })
            .FirstAsync(cancellationToken);

        // Best-effort live push AFTER the commit — a push failure never fails the send (§4). Addressed to the
        // CURRENT members (loaded above) so a removed/left user's stale socket is never a recipient (ADR 0024).
        await chatNotifier.MessageSentAsync(
            request.ConversationId,
            members,
            new ChatMessageDto(message.Id, request.ConversationId, me, sender.DisplayName, message.Body, message.SentAtUtc),
            cancellationToken);

        return new ChatMessageVm(
            message.Id,
            me,
            sender.DisplayName,
            sender.AvatarFileKey,
            message.Body,
            message.SentAtUtc,
            IsMine: true,
            IsDeleted: false);
    }
}
