using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Realtime;
using Cinora.Application.Features.Blocks;
using Cinora.Application.Features.Friends;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Chat;

/// <summary>
/// Adds members to a group conversation (§5). Any member may add their own accepted friends who are not blocked
/// (a friends-only, blocking-aware gate); ids already in the group and the caller's own id are ignored. The
/// conversation must exist (404) and be a group the caller belongs to (403 otherwise). After the commit an
/// update is pushed best-effort over <see cref="IChatNotifier"/>.
/// </summary>
/// <param name="ConversationId">The group to add to.</param>
/// <param name="MemberIds">The caller's friends to add (deduped; already-members are skipped).</param>
public sealed record AddGroupMembersCommand(Guid ConversationId, IReadOnlyList<Guid> MemberIds) : IRequest<Unit>;

/// <summary>Handles <see cref="AddGroupMembersCommand"/>.</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved acting member (ADR 0009).</param>
/// <param name="chatNotifier">The best-effort realtime push port (§4).</param>
public sealed class AddGroupMembersCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IChatNotifier chatNotifier)
    : IRequestHandler<AddGroupMembersCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(AddGroupMembersCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();

        var conversationType = await db.Conversations.AsNoTracking()
            .Where(c => c.Id == request.ConversationId)
            .Select(c => (ConversationType?)c.Type)
            .FirstOrDefaultAsync(cancellationToken);
        if (conversationType is null)
        {
            throw new NotFoundException($"Conversation ({request.ConversationId}) was not found.");
        }

        if (conversationType != ConversationType.Group)
        {
            throw new ForbiddenAccessException("Only group conversations can have members added.");
        }

        var existingMembers = await db.ConversationMembers.AsNoTracking()
            .Where(m => m.ConversationId == request.ConversationId)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
        if (!existingMembers.Contains(me))
        {
            throw new ForbiddenAccessException("You are not a member of this conversation.");
        }

        var toAdd = request.MemberIds
            .Where(id => id != me && !existingMembers.Contains(id))
            .Distinct()
            .ToList();
        if (toAdd.Count == 0)
        {
            return Unit.Value; // everyone requested is already a member — a no-op.
        }

        var friendIds = (await FriendProjections.AcceptedFriendIdsAsync(db, me, cancellationToken)).ToHashSet();
        var blocked = await BlockQueries.BlockedOrBlockedByIdsAsync(db, me, cancellationToken);
        foreach (var id in toAdd)
        {
            if (!friendIds.Contains(id) || blocked.Contains(id))
            {
                throw new ForbiddenAccessException("You can only add your friends to a group.");
            }
        }

        foreach (var id in toAdd)
        {
            db.ConversationMembers.Add(ConversationMember.Create(request.ConversationId, id, ConversationRole.Member));
        }

        await db.SaveChangesAsync(cancellationToken);

        var recipients = existingMembers.Concat(toAdd).ToList();
        await chatNotifier.ConversationChangedAsync(
            recipients, new ConversationEventDto(request.ConversationId, "member-added"), cancellationToken);

        return Unit.Value;
    }
}
