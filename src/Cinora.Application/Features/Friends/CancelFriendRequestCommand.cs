using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Friends;

/// <summary>
/// Cancels the current user's OUTGOING pending friend request (§5). The actor is server-resolved (ADR 0009); only
/// the requester may cancel their own request (<see cref="ForbiddenAccessException"/> → 403 otherwise). Idempotent
/// — a request that no longer exists is treated as already cancelled (a silent no-op). Only a
/// <see cref="FriendStatus.Pending"/> row is removed (an accepted/declined row is left untouched).
/// </summary>
/// <param name="RequestId">The id of the pending <c>Friend</c> request to cancel (the resource; from the route).</param>
public sealed record CancelFriendRequestCommand(Guid RequestId) : IRequest<Unit>;

/// <summary>
/// Handles <see cref="CancelFriendRequestCommand"/>: load the request (no-op when already gone), enforce that the
/// current user is its requester (else 403), and remove it if still pending.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (§2, ADR 0009).</param>
public sealed class CancelFriendRequestCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<CancelFriendRequestCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(CancelFriendRequestCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();

        var friend = await db.Friends.FirstOrDefaultAsync(
            candidate => candidate.Id == request.RequestId, cancellationToken);
        if (friend is null)
        {
            return Unit.Value; // idempotent — already gone
        }

        if (friend.RequesterId != me)
        {
            throw new ForbiddenAccessException("Only the requester can cancel this friend request.");
        }

        if (friend.Status == FriendStatus.Pending)
        {
            db.Friends.Remove(friend);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Unit.Value;
    }
}
