using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Realtime;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Chat;

/// <summary>
/// Removes a member from a group conversation (§5) — <b>admin-only</b>. The caller must be the group admin (403
/// otherwise); removing yourself is not allowed here (use <see cref="LeaveConversationCommand"/>). After the
/// commit an update is pushed best-effort over <see cref="IChatNotifier"/> to the remaining members and the
/// removed user.
/// </summary>
/// <param name="ConversationId">The group to remove from.</param>
/// <param name="MemberId">The member to remove.</param>
public sealed record RemoveGroupMemberCommand(Guid ConversationId, Guid MemberId) : IRequest<Unit>;

/// <summary>Handles <see cref="RemoveGroupMemberCommand"/>.</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved acting admin (ADR 0009).</param>
/// <param name="chatNotifier">The best-effort realtime push port (§4).</param>
public sealed class RemoveGroupMemberCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IChatNotifier chatNotifier)
    : IRequestHandler<RemoveGroupMemberCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(RemoveGroupMemberCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();

        var myRole = await db.ConversationMembers.AsNoTracking()
            .Where(m => m.ConversationId == request.ConversationId && m.UserId == me)
            .Select(m => (ConversationRole?)m.Role)
            .FirstOrDefaultAsync(cancellationToken);
        if (myRole is null)
        {
            throw new ForbiddenAccessException("You are not a member of this conversation.");
        }

        if (myRole != ConversationRole.Admin)
        {
            throw new ForbiddenAccessException("Only an admin can remove members.");
        }

        if (request.MemberId == me)
        {
            throw new ForbiddenAccessException("Use leave to remove yourself from a group.");
        }

        var target = await db.ConversationMembers
            .FirstOrDefaultAsync(
                m => m.ConversationId == request.ConversationId && m.UserId == request.MemberId, cancellationToken);
        if (target is null)
        {
            throw new NotFoundException($"Member ({request.MemberId}) was not found in this conversation.");
        }

        db.ConversationMembers.Remove(target);
        await db.SaveChangesAsync(cancellationToken);

        var remaining = await db.ConversationMembers.AsNoTracking()
            .Where(m => m.ConversationId == request.ConversationId)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
        var recipients = remaining.Append(request.MemberId).ToList();
        await chatNotifier.ConversationChangedAsync(
            recipients, new ConversationEventDto(request.ConversationId, "member-removed"), cancellationToken);

        return Unit.Value;
    }
}
