using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Chat;

/// <summary>
/// Advances the current user's read watermark for a conversation to now (§8) — dropping its unread count to zero
/// and letting the other participants' sent bubbles flip to "seen". Membership is required (a non-member → 403).
/// After the commit the new read position is pushed best-effort over <see cref="IChatNotifier"/>.
/// </summary>
/// <param name="ConversationId">The conversation the viewer has read.</param>
public sealed record MarkConversationReadCommand(Guid ConversationId) : IRequest<Unit>;

/// <summary>Handles <see cref="MarkConversationReadCommand"/>.</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved acting reader (ADR 0009).</param>
/// <param name="chatNotifier">The best-effort realtime push port (§4).</param>
public sealed class MarkConversationReadCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IChatNotifier chatNotifier)
    : IRequestHandler<MarkConversationReadCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(MarkConversationReadCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();

        var membership = await db.ConversationMembers
            .FirstOrDefaultAsync(m => m.ConversationId == request.ConversationId && m.UserId == me, cancellationToken);
        if (membership is null)
        {
            throw new ForbiddenAccessException("You are not a member of this conversation.");
        }

        membership.MarkRead(DateTime.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        await chatNotifier.ConversationReadAsync(
            request.ConversationId, me, membership.LastReadAtUtc, cancellationToken);

        return Unit.Value;
    }
}
