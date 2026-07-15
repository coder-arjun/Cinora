using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Blocks;

/// <summary>
/// Removes the current user's block against <paramref name="TargetUserId"/> (§5). Idempotent — a no-op when no
/// such block exists. Does NOT restore any prior friendship (the other party must send a fresh request).
/// </summary>
/// <param name="TargetUserId">The id of the user to unblock (the resource).</param>
public sealed record UnblockUserCommand(Guid TargetUserId) : IRequest<Unit>;

/// <summary>Handles <see cref="UnblockUserCommand"/>.</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved acting user.</param>
public sealed class UnblockUserCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<UnblockUserCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(UnblockUserCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();
        var target = request.TargetUserId;

        var block = await db.UserBlocks.FirstOrDefaultAsync(
            candidate => candidate.BlockerId == me && candidate.BlockedUserId == target, cancellationToken);
        if (block is not null)
        {
            db.UserBlocks.Remove(block);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Unit.Value;
    }
}
