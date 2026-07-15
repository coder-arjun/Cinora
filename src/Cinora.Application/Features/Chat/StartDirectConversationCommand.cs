using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Blocks;
using Cinora.Application.Features.Friends;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Chat;

/// <summary>
/// Finds-or-creates the 1:1 conversation between the current user and <see cref="OtherUserId"/> (§3). Only an
/// accepted friend who is not blocked may be messaged; a second call for the same pair returns the existing
/// conversation rather than a duplicate. The actor is server-resolved (ADR 0009).
/// </summary>
/// <param name="OtherUserId">The friend to open a direct chat with.</param>
public sealed record StartDirectConversationCommand(Guid OtherUserId) : IRequest<Guid>;

/// <summary>Handles <see cref="StartDirectConversationCommand"/>.</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved acting user.</param>
public sealed class StartDirectConversationCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<StartDirectConversationCommand, Guid>
{
    /// <inheritdoc />
    public async Task<Guid> Handle(StartDirectConversationCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();
        var other = request.OtherUserId;

        if (await BlockQueries.AreBlockedEitherWayAsync(db, me, other, cancellationToken))
        {
            throw new ForbiddenAccessException("You cannot message this user.");
        }

        var friendIds = await FriendProjections.AcceptedFriendIdsAsync(db, me, cancellationToken);
        if (!friendIds.Contains(other))
        {
            throw new ForbiddenAccessException("You can only message your friends.");
        }

        // Find an existing Direct conversation whose member set is exactly {me, other}.
        var existingId = await db.Conversations.AsNoTracking()
            .Where(c => c.Type == ConversationType.Direct
                && db.ConversationMembers.Count(m => m.ConversationId == c.Id) == 2
                && db.ConversationMembers.Any(m => m.ConversationId == c.Id && m.UserId == me)
                && db.ConversationMembers.Any(m => m.ConversationId == c.Id && m.UserId == other))
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (existingId is { } id)
        {
            return id;
        }

        var convo = Conversation.CreateDirect(me, other);
        db.Conversations.Add(convo);
        db.ConversationMembers.Add(ConversationMember.Create(convo.Id, me, ConversationRole.Member));
        db.ConversationMembers.Add(ConversationMember.Create(convo.Id, other, ConversationRole.Member));
        await db.SaveChangesAsync(cancellationToken);
        return convo.Id;
    }
}
