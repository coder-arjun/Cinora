using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;

namespace Cinora.Application.Features.Friends;

/// <summary>
/// Returns the current user's accepted friends (both request directions), projected to the "other" user's
/// display info (§7.1). Drives the friends list on <c>/friends</c>. Requires authentication (the endpoint is
/// <c>[Authorize]</c>); the actor is server-resolved.
/// </summary>
public sealed record GetFriendsQuery : IRequest<FriendsVm>;

/// <summary>Handles <see cref="GetFriendsQuery"/> via the shared <see cref="FriendProjections"/> read (no N+1).</summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved current user whose friends to list.</param>
public sealed class GetFriendsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetFriendsQuery, FriendsVm>
{
    /// <inheritdoc />
    public async Task<FriendsVm> Handle(GetFriendsQuery request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();
        var friends = await FriendProjections.AcceptedFriendsAsync(db, me, cancellationToken);
        return new FriendsVm(friends);
    }
}
