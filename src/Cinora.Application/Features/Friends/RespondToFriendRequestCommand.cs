using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Realtime;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinora.Application.Features.Friends;

/// <summary>
/// Accepts or declines a pending friend request (Milestone 3.3). Ownership is enforced inside the unit of work:
/// only the request's <c>AddresseeId</c> may respond — a requester or third party attempting it throws
/// <see cref="ForbiddenAccessException"/> (→ 403); a missing request throws <see cref="NotFoundException"/>
/// (→ 404). The domain rejects responding to a non-pending request (<see cref="Cinora.Domain.Exceptions.DomainException"/> → 400). On
/// accept, a <see cref="NotificationType.FriendAccepted"/> notification is co-persisted to the requester in the
/// same <c>SaveChanges</c> (§9.1 — the row only; the live push arrives in 3.5).
/// </summary>
/// <param name="RequestId">The id of the <c>Friend</c> request to respond to (the resource; from the route).</param>
/// <param name="Accept"><c>true</c> to accept the request; <c>false</c> to decline it.</param>
public sealed record RespondToFriendRequestCommand(Guid RequestId, bool Accept) : IRequest<Unit>;

/// <summary>
/// Handles <see cref="RespondToFriendRequestCommand"/>: load the tracked request (404 if missing), enforce that
/// the current user is the addressee (403 otherwise), then apply the domain transition
/// (<see cref="Friend.Accept"/>/<see cref="Friend.Decline"/>, which reject a non-pending request) and — on
/// accept — co-persist the requester's <c>FriendAccepted</c> notification before saving.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (§2, ADR 0009).</param>
/// <param name="realtime">The realtime push port; a best-effort live push runs after a committed accept notification (§9.2).</param>
/// <param name="pushDispatch">The Web Push fan-out port (ADR 0020); enqueued by the shared dispatcher after the SignalR push.</param>
/// <param name="logger">Records a Warning if the best-effort push fails (the accept/notification still commit).</param>
public sealed class RespondToFriendRequestCommandHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    IRealtimeNotifier realtime,
    IPushDispatch pushDispatch,
    ILogger<RespondToFriendRequestCommandHandler> logger)
    : IRequestHandler<RespondToFriendRequestCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(RespondToFriendRequestCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();

        var friend = await db.Friends
            .FirstOrDefaultAsync(candidate => candidate.Id == request.RequestId, cancellationToken)
            ?? throw new NotFoundException($"Friend request ({request.RequestId}) was not found.");

        if (friend.AddresseeId != me)
        {
            throw new ForbiddenAccessException("Only the addressee can respond to this friend request.");
        }

        Notification? createdNotification = null;
        string? accepterName = null;
        if (request.Accept)
        {
            friend.Accept();

            accepterName = await db.Users
                .AsNoTracking()
                .Where(user => user.Id == me)
                .Select(user => user.DisplayName)
                .FirstAsync(cancellationToken);

            createdNotification = Notification.Create(
                friend.RequesterId,
                NotificationType.FriendAccepted,
                $"{accepterName} accepted your friend request",
                actorUserId: me,
                targetId: friend.Id);
            db.Notifications.Add(createdNotification);
        }
        else
        {
            friend.Decline();
        }

        await db.SaveChangesAsync(cancellationToken);

        // Best-effort live push AFTER the commit — a push failure never fails the accept (§9.2). Only on accept
        // (a decline notifies no one).
        if (createdNotification is not null)
        {
            var dto = new NotificationDto(
                createdNotification.Id,
                createdNotification.Type,
                createdNotification.Message,
                accepterName,
                createdNotification.TargetId,
                createdNotification.CreatedAtUtc);

            await RealtimeNotificationDispatcher.DispatchBestEffortAsync(
                realtime, db, pushDispatch, logger, friend.RequesterId, dto, cancellationToken);
        }

        return Unit.Value;
    }
}
