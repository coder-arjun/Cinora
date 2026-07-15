using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Friends;

/// <summary>
/// Removes the accepted friendship between the current user and <paramref name="OtherUserId"/> (Milestone 3.3).
/// The acting user is server-resolved (ADR 0009); the command carries only the other party's id. It is
/// idempotent: if the two are not accepted friends (in either direction), it is a silent no-op. Only an
/// <see cref="FriendStatus.Accepted"/> row is removed — a still-pending request is not a friendship and is left
/// untouched.
/// </summary>
/// <param name="OtherUserId">The id of the friend to remove (the resource; from the route).</param>
public sealed record RemoveFriendCommand(Guid OtherUserId) : IRequest<Unit>;

/// <summary>
/// Handles <see cref="RemoveFriendCommand"/>: find the single accepted <c>Friend</c> row between the two users
/// (whichever direction it was requested in) and remove it; a no-op when none exists.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (§2, ADR 0009).</param>
public sealed class RemoveFriendCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<RemoveFriendCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(RemoveFriendCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();
        var other = request.OtherUserId;

        var friend = await db.Friends.FirstOrDefaultAsync(
            candidate => candidate.Status == FriendStatus.Accepted
                && ((candidate.RequesterId == me && candidate.AddresseeId == other)
                    || (candidate.RequesterId == other && candidate.AddresseeId == me)),
            cancellationToken);

        if (friend is not null)
        {
            db.Friends.Remove(friend);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Unit.Value;
    }
}
