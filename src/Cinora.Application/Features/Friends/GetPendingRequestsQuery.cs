using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Friends;

/// <summary>
/// Returns the current user's pending friend requests, split into incoming (addressed to them, which they may
/// accept/decline) and outgoing (which they sent, awaiting a response) — §7.1. Each request carries the other
/// party's display info plus the <c>Friend</c> row id so the incoming actions can target it. Requires
/// authentication; the actor is server-resolved.
/// </summary>
public sealed record GetPendingRequestsQuery : IRequest<PendingRequestsVm>;

/// <summary>
/// Handles <see cref="GetPendingRequestsQuery"/> with two <c>AsNoTracking</c> projections (incoming/outgoing).
/// Each row resolves the other party's display fields (name + avatar) via a SINGLE correlated <c>Users</c>
/// sub-query — folding both fields into one <see cref="FriendUserRow"/> read rather than two — so the query
/// stays N+1-free (§13); the small in-memory map completes the <see cref="PendingRequestVm"/>. Newest-first by
/// request time.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved current user whose pending requests to list.</param>
public sealed class GetPendingRequestsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetPendingRequestsQuery, PendingRequestsVm>
{
    /// <inheritdoc />
    public async Task<PendingRequestsVm> Handle(GetPendingRequestsQuery request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();

        var incomingRows = await db.Friends
            .AsNoTracking()
            .Where(friend => friend.AddresseeId == me && friend.Status == FriendStatus.Pending)
            .OrderByDescending(friend => friend.RequestedAtUtc)
            .Select(friend => new PendingRow
            {
                RequestId = friend.Id,
                OtherId = friend.RequesterId,
                Other = db.Users
                    .Where(user => user.Id == friend.RequesterId)
                    .Select(user => new FriendUserRow
                    {
                        DisplayName = user.DisplayName,
                        AvatarFileKey = user.AvatarFileKey,
                    })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var outgoingRows = await db.Friends
            .AsNoTracking()
            .Where(friend => friend.RequesterId == me && friend.Status == FriendStatus.Pending)
            .OrderByDescending(friend => friend.RequestedAtUtc)
            .Select(friend => new PendingRow
            {
                RequestId = friend.Id,
                OtherId = friend.AddresseeId,
                Other = db.Users
                    .Where(user => user.Id == friend.AddresseeId)
                    .Select(user => new FriendUserRow
                    {
                        DisplayName = user.DisplayName,
                        AvatarFileKey = user.AvatarFileKey,
                    })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var incoming = incomingRows.Select(row => ToVm(row, isIncoming: true)).ToList();
        var outgoing = outgoingRows.Select(row => ToVm(row, isIncoming: false)).ToList();

        return new PendingRequestsVm(incoming, outgoing);
    }

    private static PendingRequestVm ToVm(PendingRow row, bool isIncoming) => new(
        row.RequestId,
        row.OtherId,
        row.Other?.DisplayName ?? string.Empty,
        row.Other?.AvatarFileKey,
        isIncoming);
}

/// <summary>The intermediate shape EF materializes for a pending-request projection; mapped to <see cref="PendingRequestVm"/> in memory.</summary>
internal sealed class PendingRow
{
    /// <summary>The <c>Friend</c> request row id (the resource the incoming actions target).</summary>
    public required Guid RequestId { get; init; }

    /// <summary>The "other" party's user id (the requester on an incoming row, the addressee on an outgoing one).</summary>
    public required Guid OtherId { get; init; }

    /// <summary>The other party's display fields, resolved by one correlated <c>Users</c> sub-query.</summary>
    public FriendUserRow? Other { get; init; }
}
