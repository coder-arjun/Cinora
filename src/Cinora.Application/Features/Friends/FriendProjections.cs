using Cinora.Application.Common.Interfaces;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Friends;

/// <summary>
/// Shared, translatable friend read helpers reused by <see cref="GetFriendsQuery"/> and
/// <see cref="Profiles.GetProfileQuery"/> so the "accepted friends of a user" shape cannot drift (DRY). The
/// "other" party's display name/avatar are resolved by a SINGLE correlated sub-query against <c>Users</c>
/// (both fields in one read), mirroring the review/comment projections — one <c>AsNoTracking</c> query, no
/// N+1 (§13). The directed <c>Friend</c> graph stores each accepted relationship once (in whichever direction
/// it was requested), so "friends of X" is every accepted row where X is EITHER the requester OR the addressee,
/// projecting the party that is NOT X.
/// </summary>
internal static class FriendProjections
{
    /// <summary>
    /// Loads a user's accepted friends (both request directions), each projected to a <see cref="FriendVm"/>
    /// and ordered by display name. The conditional "other id" (<c>Requester == user ? Addressee : Requester</c>)
    /// and the folded author sub-query both translate to SQL; ordering is applied in memory after materialize
    /// because it keys off the projected VM.
    /// </summary>
    /// <param name="db">The persistence context whose sets back the query.</param>
    /// <param name="userId">The user whose accepted friends to load.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous read.</param>
    /// <returns>The user's accepted friends, display-name-ordered; empty when they have none.</returns>
    public static async Task<IReadOnlyList<FriendVm>> AcceptedFriendsAsync(
        IAppDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.Friends
            .AsNoTracking()
            .Where(friend => friend.Status == FriendStatus.Accepted
                && (friend.RequesterId == userId || friend.AddresseeId == userId))
            .Select(friend => new FriendRow
            {
                OtherId = friend.RequesterId == userId ? friend.AddresseeId : friend.RequesterId,
                Other = db.Users
                    .Where(user => user.Id
                        == (friend.RequesterId == userId ? friend.AddresseeId : friend.RequesterId))
                    .Select(user => new FriendUserRow
                    {
                        DisplayName = user.DisplayName,
                        AvatarFileKey = user.AvatarFileKey,
                    })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new FriendVm(row.OtherId, row.Other?.DisplayName ?? string.Empty, row.Other?.AvatarFileKey))
            .OrderBy(vm => vm.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Resolves a user's accepted-friend id SET (both request directions), projecting the OTHER party's id — the
    /// bounded <c>IN (@friendIds)</c> parameter set the activity feed pages friends' reviews over (§8, ADR 0012).
    /// One <c>AsNoTracking</c> read; the conditional "other id" translates to SQL. No display fields are loaded
    /// (the feed re-resolves those per review), so this stays a lean id list.
    /// </summary>
    /// <param name="db">The persistence context whose sets back the query.</param>
    /// <param name="userId">The user whose accepted-friend ids to load.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous read.</param>
    /// <returns>The ids of the user's accepted friends; empty when they have none.</returns>
    public static Task<List<Guid>> AcceptedFriendIdsAsync(
        IAppDbContext db, Guid userId, CancellationToken cancellationToken) =>
        db.Friends
            .AsNoTracking()
            .Where(friend => friend.Status == FriendStatus.Accepted
                && (friend.RequesterId == userId || friend.AddresseeId == userId))
            .Select(friend => friend.RequesterId == userId ? friend.AddresseeId : friend.RequesterId)
            .ToListAsync(cancellationToken);

    /// <summary>Counts a user's accepted friends (both directions) without materializing the list.</summary>
    /// <param name="db">The persistence context whose sets back the query.</param>
    /// <param name="userId">The user whose accepted friends to count.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous read.</param>
    /// <returns>The number of accepted friends.</returns>
    public static Task<int> CountAcceptedFriendsAsync(
        IAppDbContext db, Guid userId, CancellationToken cancellationToken) =>
        db.Friends
            .AsNoTracking()
            .CountAsync(
                friend => friend.Status == FriendStatus.Accepted
                    && (friend.RequesterId == userId || friend.AddresseeId == userId),
                cancellationToken);
}

/// <summary>The intermediate shape EF materializes for a friend projection; mapped to <see cref="FriendVm"/> in memory.</summary>
internal sealed class FriendRow
{
    /// <summary>The "other" party's user id (the friend, from the current user's perspective).</summary>
    public required Guid OtherId { get; init; }

    /// <summary>The other party's display fields, resolved by one correlated <c>Users</c> sub-query.</summary>
    public FriendUserRow? Other { get; init; }
}

/// <summary>The user fields materialized by the single correlated <c>Users</c> sub-query in the friend projections.</summary>
internal sealed class FriendUserRow
{
    /// <summary>The user's public display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The user's avatar storage key, or <c>null</c>.</summary>
    public string? AvatarFileKey { get; init; }
}
