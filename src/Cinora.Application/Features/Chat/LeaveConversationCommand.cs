using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Realtime;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Chat;

/// <summary>
/// Removes the current user's membership from a conversation (§5). If the leaver was the group
/// <see cref="ConversationRole.Admin"/> and members remain, the earliest-joined remaining member is promoted to
/// admin so the group is never left admin-less (§3). The actor is server-resolved (ADR 0009). After the commit
/// an update is pushed best-effort over <see cref="IChatNotifier"/> to the remaining members.
/// </summary>
/// <param name="ConversationId">The conversation to leave.</param>
public sealed record LeaveConversationCommand(Guid ConversationId) : IRequest<Unit>;

/// <summary>Handles <see cref="LeaveConversationCommand"/>.</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved leaving user (ADR 0009).</param>
/// <param name="chatNotifier">The best-effort realtime push port (§4).</param>
public sealed class LeaveConversationCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IChatNotifier chatNotifier)
    : IRequestHandler<LeaveConversationCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(LeaveConversationCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();

        var myMembership = await db.ConversationMembers
            .FirstOrDefaultAsync(m => m.ConversationId == request.ConversationId && m.UserId == me, cancellationToken);
        if (myMembership is null)
        {
            throw new ForbiddenAccessException("You are not a member of this conversation.");
        }

        var wasAdmin = myMembership.Role == ConversationRole.Admin;
        db.ConversationMembers.Remove(myMembership);

        // If the admin leaves, promote the earliest-joined remaining member — unless another admin already
        // remains — so the group is never admin-less while it still has members.
        if (wasAdmin)
        {
            var remaining = await db.ConversationMembers
                .Where(m => m.ConversationId == request.ConversationId && m.UserId != me)
                .OrderBy(m => m.JoinedAtUtc).ThenBy(m => m.Id)
                .ToListAsync(cancellationToken);
            if (remaining.Count > 0 && !remaining.Exists(m => m.Role == ConversationRole.Admin))
            {
                remaining[0].Promote();
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        var membersLeft = await db.ConversationMembers.AsNoTracking()
            .Where(m => m.ConversationId == request.ConversationId)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
        await chatNotifier.ConversationChangedAsync(
            membersLeft, new ConversationEventDto(request.ConversationId, "left"), cancellationToken);

        return Unit.Value;
    }
}
