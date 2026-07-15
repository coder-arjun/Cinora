using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Blocks;

/// <summary>
/// Blocks <paramref name="TargetUserId"/> as the current user (§2, §5). The actor is server-resolved (ADR 0009).
/// In one unit of work it creates the <see cref="UserBlock"/> and removes EVERY <see cref="Friend"/> row for the
/// unordered pair (any status, either direction), fully cutting the two apart. Idempotent — an existing block is
/// a no-op. SILENT — no notification is created (§9). A self-block throws <see cref="Cinora.Domain.Exceptions.DomainException"/> (→ 400).
/// </summary>
/// <param name="TargetUserId">The id of the user to block (the resource).</param>
public sealed record BlockUserCommand(Guid TargetUserId) : IRequest<Unit>;

/// <summary>Handles <see cref="BlockUserCommand"/>.</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved acting user.</param>
public sealed class BlockUserCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<BlockUserCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(BlockUserCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();
        var target = request.TargetUserId;

        var targetExists = await db.Users.AsNoTracking().AnyAsync(user => user.Id == target, cancellationToken);
        if (!targetExists)
        {
            throw new NotFoundException($"User ({target}) was not found.");
        }

        var alreadyBlocked = await db.UserBlocks
            .AnyAsync(block => block.BlockerId == me && block.BlockedUserId == target, cancellationToken);
        if (alreadyBlocked)
        {
            return Unit.Value; // idempotent no-op
        }

        // UserBlock.Create throws DomainException (→ 400) on a self-block, before anything is staged.
        db.UserBlocks.Add(UserBlock.Create(me, target));

        // Full cut-off: remove any friendship or pending request between the two (either direction).
        var pairRows = await db.Friends
            .Where(friend => (friend.RequesterId == me && friend.AddresseeId == target)
                || (friend.RequesterId == target && friend.AddresseeId == me))
            .ToListAsync(cancellationToken);
        db.Friends.RemoveRange(pairRows);

        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}
