using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Chat;

/// <summary>
/// Fetches the header/metadata of a conversation the viewer belongs to (§6): its display title + avatar, whether
/// the viewer is the group admin, and the full member roster (for the group-management panel). Membership is
/// enforced (a non-member → 403, a missing conversation → 404). Read-only (CQRS-pure).
/// </summary>
/// <param name="ConversationId">The conversation whose header to read.</param>
public sealed record GetConversationHeaderQuery(Guid ConversationId) : IRequest<ConversationHeaderVm>;

/// <summary>Handles <see cref="GetConversationHeaderQuery"/>.</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved viewer (ADR 0009).</param>
public sealed class GetConversationHeaderQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetConversationHeaderQuery, ConversationHeaderVm>
{
    /// <inheritdoc />
    public async Task<ConversationHeaderVm> Handle(
        GetConversationHeaderQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();

        var conversation = await db.Conversations.AsNoTracking()
            .Where(c => c.Id == request.ConversationId)
            .Select(c => new { c.Id, c.Type, c.Title })
            .FirstOrDefaultAsync(cancellationToken);
        if (conversation is null)
        {
            throw new NotFoundException($"Conversation ({request.ConversationId}) was not found.");
        }

        var members = await db.ConversationMembers.AsNoTracking()
            .Where(m => m.ConversationId == request.ConversationId)
            .OrderBy(m => m.JoinedAtUtc).ThenBy(m => m.Id)
            .Select(m => new ChatMemberVm(
                m.UserId,
                db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefault()!,
                db.Users.Where(u => u.Id == m.UserId).Select(u => u.AvatarFileKey).FirstOrDefault(),
                m.Role == ConversationRole.Admin))
            .ToListAsync(cancellationToken);

        var myMembership = members.FirstOrDefault(m => m.UserId == me);
        if (myMembership is null)
        {
            throw new ForbiddenAccessException("You are not a member of this conversation.");
        }

        string title;
        string? avatarFileKey;
        if (conversation.Type == ConversationType.Direct)
        {
            var other = members.FirstOrDefault(m => m.UserId != me);
            title = other?.DisplayName ?? string.Empty;
            avatarFileKey = other?.AvatarFileKey;
        }
        else
        {
            title = conversation.Title ?? string.Empty;
            avatarFileKey = null;
        }

        return new ConversationHeaderVm(
            conversation.Id, conversation.Type, title, avatarFileKey, myMembership.IsAdmin, members);
    }
}
