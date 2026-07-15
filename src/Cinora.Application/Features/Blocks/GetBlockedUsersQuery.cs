using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Blocks;

/// <summary>Lists the users the current user has blocked (§5), newest-first, for the Blocked tab.</summary>
public sealed record GetBlockedUsersQuery : IRequest<BlockedUsersVm>;

/// <summary>The current user's blocked list.</summary>
/// <param name="Blocked">The blocked users, newest block first.</param>
public sealed record BlockedUsersVm(IReadOnlyList<BlockedUserVm> Blocked);

/// <summary>One blocked user, projected for the Blocked-tab row (name + avatar only).</summary>
/// <param name="UserId">The blocked user's id (the Unblock action targets it).</param>
/// <param name="DisplayName">The blocked user's display name.</param>
/// <param name="AvatarFileKey">The blocked user's avatar key, or <c>null</c> for a monogram.</param>
public sealed record BlockedUserVm(Guid UserId, string DisplayName, string? AvatarFileKey);

/// <summary>Handles <see cref="GetBlockedUsersQuery"/>.</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved viewer.</param>
public sealed class GetBlockedUsersQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetBlockedUsersQuery, BlockedUsersVm>
{
    /// <inheritdoc />
    public async Task<BlockedUsersVm> Handle(GetBlockedUsersQuery request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();

        var blocked = await db.UserBlocks
            .AsNoTracking()
            .Where(block => block.BlockerId == me)
            .OrderByDescending(block => block.CreatedAtUtc)
            .Select(block => db.Users
                .Where(user => user.Id == block.BlockedUserId)
                .Select(user => new BlockedUserVm(user.Id, user.DisplayName, user.AvatarFileKey))
                .First())
            .ToListAsync(cancellationToken);

        return new BlockedUsersVm(blocked);
    }
}
