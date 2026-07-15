using Cinora.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Web.Hubs;

/// <summary>
/// A tiny scoped service the <see cref="ChatHub"/> uses to answer "is this user a member of this conversation?"
/// without pulling an Application handler (or its pipeline) into the transport layer (§4). It is the hub's ONLY
/// database touch — keeping the hub a thin dispatcher (§12).
/// </summary>
public interface IChatMembership
{
    /// <summary>Determines whether the user is a member of the conversation.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="userId">The user.</param>
    /// <param name="ct">A token to observe for cancellation.</param>
    /// <returns><see langword="true"/> when a membership row exists for the pair.</returns>
    Task<bool> IsMemberAsync(Guid conversationId, Guid userId, CancellationToken ct);
}

/// <summary>Implements <see cref="IChatMembership"/> as a cheap indexed <c>AnyAsync</c> over the scoped <see cref="IAppDbContext"/>.</summary>
/// <param name="db">The per-request persistence context.</param>
public sealed class ChatMembership(IAppDbContext db) : IChatMembership
{
    /// <inheritdoc />
    public Task<bool> IsMemberAsync(Guid conversationId, Guid userId, CancellationToken ct) =>
        db.ConversationMembers.AsNoTracking()
            .AnyAsync(m => m.ConversationId == conversationId && m.UserId == userId, ct);
}
