using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Chat;

/// <summary>
/// Counts the current user's total unread chat messages across every conversation they belong to (§9) — the
/// number behind the nav chat badge. Unread = messages from someone else sent after the viewer's per-conversation
/// read watermark. One <c>AsNoTracking</c> aggregate, no N+1. Read-only (CQRS-pure).
/// </summary>
public sealed record GetChatUnreadCountQuery : IRequest<int>;

/// <summary>Handles <see cref="GetChatUnreadCountQuery"/>.</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved viewer (ADR 0009).</param>
public sealed class GetChatUnreadCountQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetChatUnreadCountQuery, int>
{
    /// <inheritdoc />
    public async Task<int> Handle(GetChatUnreadCountQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();

        return await db.Messages.AsNoTracking()
            .CountAsync(
                msg => msg.SenderId != me
                    && db.ConversationMembers.Any(m => m.UserId == me
                        && m.ConversationId == msg.ConversationId
                        && msg.SentAtUtc > m.LastReadAtUtc),
                cancellationToken);
    }
}
