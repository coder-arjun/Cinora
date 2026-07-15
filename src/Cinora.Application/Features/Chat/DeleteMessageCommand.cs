using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Chat;

/// <summary>
/// Soft-deletes a message (§5) — <b>sender-only</b>. Only the original sender may delete their message (403
/// otherwise); the row is kept and tombstoned via <see cref="Cinora.Domain.Entities.Message.MarkDeleted"/> so the
/// keyset history and read positions stay stable and the view renders a "message deleted" placeholder. The actor
/// is server-resolved (ADR 0009); the tombstoned bubble is returned so the caller can swap it in place.
/// </summary>
/// <remarks>
/// No realtime push is emitted: <see cref="IChatNotifier"/> models message-send, read, and conversation-change
/// events, not per-message deletion — the tombstone surfaces for other participants on their next history load. A
/// dedicated live-delete signal is a candidate follow-up (noted for the final review), deliberately out of scope.
/// </remarks>
/// <param name="MessageId">The message to delete.</param>
public sealed record DeleteMessageCommand(Guid MessageId) : IRequest<ChatMessageVm>;

/// <summary>Handles <see cref="DeleteMessageCommand"/>.</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved acting sender (ADR 0009).</param>
public sealed class DeleteMessageCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<DeleteMessageCommand, ChatMessageVm>
{
    /// <inheritdoc />
    public async Task<ChatMessageVm> Handle(DeleteMessageCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();

        var message = await db.Messages
            .FirstOrDefaultAsync(m => m.Id == request.MessageId, cancellationToken); // tracked
        if (message is null)
        {
            throw new NotFoundException($"Message ({request.MessageId}) was not found.");
        }

        if (message.SenderId != me)
        {
            throw new ForbiddenAccessException("You can only delete your own messages.");
        }

        message.MarkDeleted();
        await db.SaveChangesAsync(cancellationToken);

        var sender = await db.Users.AsNoTracking()
            .Where(u => u.Id == me)
            .Select(u => new { u.DisplayName, u.AvatarFileKey })
            .FirstAsync(cancellationToken);

        return new ChatMessageVm(
            message.Id, me, sender.DisplayName, sender.AvatarFileKey,
            string.Empty, message.SentAtUtc, IsMine: true, IsDeleted: true);
    }
}
