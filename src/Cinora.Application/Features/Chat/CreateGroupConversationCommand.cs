using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Realtime;
using Cinora.Application.Features.Blocks;
using Cinora.Application.Features.Friends;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using FluentValidation;

namespace Cinora.Application.Features.Chat;

/// <summary>
/// Creates a named group conversation owned by the current user (§3, §5). Every seed member must be one of the
/// creator's accepted friends who is not blocked; the creator becomes the group <see cref="ConversationRole.Admin"/>
/// and each seed member joins as a <see cref="ConversationRole.Member"/>. The actor is server-resolved (ADR 0009).
/// </summary>
/// <param name="Title">The group name; required, max <see cref="Conversation.TitleMaxLength"/> characters.</param>
/// <param name="MemberIds">The friends to seed the group with (deduped; the creator is added automatically).</param>
public sealed record CreateGroupConversationCommand(string Title, IReadOnlyList<Guid> MemberIds) : IRequest<Guid>;

/// <summary>Validates <see cref="CreateGroupConversationCommand"/>: a non-blank bounded title and at least one member.</summary>
public sealed class CreateGroupConversationCommandValidator : AbstractValidator<CreateGroupConversationCommand>
{
    /// <summary>Configures the rules for <see cref="CreateGroupConversationCommand"/>.</summary>
    public CreateGroupConversationCommandValidator()
    {
        RuleFor(command => command.Title)
            .NotEmpty().WithMessage("A group name is required.")
            .MaximumLength(Conversation.TitleMaxLength)
                .WithMessage($"A group name must be at most {Conversation.TitleMaxLength} characters.");

        RuleFor(command => command.MemberIds)
            .NotEmpty().WithMessage("A group needs at least one other member.");
    }
}

/// <summary>Handles <see cref="CreateGroupConversationCommand"/>.</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved creator/admin (ADR 0009).</param>
/// <param name="chatNotifier">The best-effort realtime push port (§4).</param>
public sealed class CreateGroupConversationCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IChatNotifier chatNotifier)
    : IRequestHandler<CreateGroupConversationCommand, Guid>
{
    /// <inheritdoc />
    public async Task<Guid> Handle(CreateGroupConversationCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();
        var seedMembers = request.MemberIds.Where(id => id != me).Distinct().ToList();
        if (seedMembers.Count == 0)
        {
            throw new ForbiddenAccessException("A group needs at least one other member.");
        }

        var friendIds = (await FriendProjections.AcceptedFriendIdsAsync(db, me, cancellationToken)).ToHashSet();
        var blocked = await BlockQueries.BlockedOrBlockedByIdsAsync(db, me, cancellationToken);
        foreach (var id in seedMembers)
        {
            if (!friendIds.Contains(id) || blocked.Contains(id))
            {
                throw new ForbiddenAccessException("You can only add your friends to a group.");
            }
        }

        var conversation = Conversation.CreateGroup(me, request.Title);
        db.Conversations.Add(conversation);
        db.ConversationMembers.Add(ConversationMember.Create(conversation.Id, me, ConversationRole.Admin));
        foreach (var id in seedMembers)
        {
            db.ConversationMembers.Add(ConversationMember.Create(conversation.Id, id, ConversationRole.Member));
        }

        await db.SaveChangesAsync(cancellationToken);

        var allMembers = seedMembers.Prepend(me).ToList();
        await chatNotifier.ConversationChangedAsync(
            allMembers, new ConversationEventDto(conversation.Id, "created"), cancellationToken);

        return conversation.Id;
    }
}
