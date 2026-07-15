using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Realtime;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Chat;

/// <summary>
/// Renames a group conversation (§5) — <b>admin-only</b>. The conversation must exist (404) and the caller must
/// be its admin (403 otherwise); the domain <see cref="Conversation.Rename"/> rejects a non-group or an invalid
/// title (→ 400). After the commit an update is pushed best-effort over <see cref="IChatNotifier"/>.
/// </summary>
/// <param name="ConversationId">The group to rename.</param>
/// <param name="Title">The new group name; required, max <see cref="Conversation.TitleMaxLength"/> characters.</param>
public sealed record RenameGroupCommand(Guid ConversationId, string Title) : IRequest<Unit>;

/// <summary>Validates <see cref="RenameGroupCommand"/>: a non-blank, bounded title.</summary>
public sealed class RenameGroupCommandValidator : AbstractValidator<RenameGroupCommand>
{
    /// <summary>Configures the rules for <see cref="RenameGroupCommand"/>.</summary>
    public RenameGroupCommandValidator() =>
        RuleFor(command => command.Title)
            .NotEmpty().WithMessage("A group name is required.")
            .MaximumLength(Conversation.TitleMaxLength)
                .WithMessage($"A group name must be at most {Conversation.TitleMaxLength} characters.");
}

/// <summary>Handles <see cref="RenameGroupCommand"/>.</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved acting admin (ADR 0009).</param>
/// <param name="chatNotifier">The best-effort realtime push port (§4).</param>
public sealed class RenameGroupCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IChatNotifier chatNotifier)
    : IRequestHandler<RenameGroupCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(RenameGroupCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();

        var conversation = await db.Conversations
            .FirstOrDefaultAsync(c => c.Id == request.ConversationId, cancellationToken); // tracked
        if (conversation is null)
        {
            throw new NotFoundException($"Conversation ({request.ConversationId}) was not found.");
        }

        var myRole = await db.ConversationMembers.AsNoTracking()
            .Where(m => m.ConversationId == request.ConversationId && m.UserId == me)
            .Select(m => (ConversationRole?)m.Role)
            .FirstOrDefaultAsync(cancellationToken);
        if (myRole != ConversationRole.Admin)
        {
            throw new ForbiddenAccessException("Only an admin can rename the group.");
        }

        conversation.Rename(request.Title); // domain guard: non-group / invalid title → DomainException → 400
        await db.SaveChangesAsync(cancellationToken);

        var members = await db.ConversationMembers.AsNoTracking()
            .Where(m => m.ConversationId == request.ConversationId)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
        await chatNotifier.ConversationChangedAsync(
            members, new ConversationEventDto(request.ConversationId, "renamed"), cancellationToken);

        return Unit.Value;
    }
}
