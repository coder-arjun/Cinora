using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Realtime;
using Cinora.Application.Features.Blocks;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinora.Application.Features.Friends;

/// <summary>
/// Sends a friend request from the current user to <paramref name="AddresseeUserId"/> (Milestone 3.3). The
/// acting requester is server-resolved (ADR 0009); the command carries only the addressee's id (the resource).
/// The directed <c>Friend</c> unique index <c>(RequesterId, AddresseeId)</c> lets <c>(A,B)</c> and <c>(B,A)</c>
/// coexist, so the handler enforces the one-relationship-per-unordered-pair rule by checking BOTH directions
/// first (§7.2). A brand-new request co-persists a <see cref="NotificationType.FriendRequest"/> notification to
/// the addressee in the same unit of work (§9.1 — the row only; the live push behind <c>IRealtimeNotifier</c>
/// arrives in 3.5).
/// </summary>
/// <param name="AddresseeUserId">The id of the user to befriend (the resource; from the form).</param>
public sealed record SendFriendRequestCommand(Guid AddresseeUserId) : IRequest<SendFriendRequestResult>;

/// <summary>How a <see cref="SendFriendRequestCommand"/> resolved against the existing relationship state (§7.2).</summary>
public enum FriendRequestOutcome
{
    /// <summary>A fresh pending request row was created (and the addressee notified).</summary>
    Created = 0,

    /// <summary>The two users are already accepted friends — a no-op.</summary>
    AlreadyFriends = 1,

    /// <summary>The current user already has a pending outgoing request to the addressee — a no-op.</summary>
    AlreadyRequested = 2,

    /// <summary>The addressee already has a pending request to the current user — no second row is created;
    /// the caller should be prompted to accept theirs instead.</summary>
    ReciprocalPending = 3,

    /// <summary>A block exists in either direction — the request is silently not created (a neutral message
    /// identical to success is shown so the block is never revealed, §14).</summary>
    Blocked = 4,
}

/// <summary>The outcome of a <see cref="SendFriendRequestCommand"/>.</summary>
/// <param name="Outcome">Which branch of the directed-pair guard the request resolved to.</param>
public sealed record SendFriendRequestResult(FriendRequestOutcome Outcome);

/// <summary>Validates <see cref="SendFriendRequestCommand"/>: the addressee id must be present (a friendly 400).</summary>
public sealed class SendFriendRequestCommandValidator : AbstractValidator<SendFriendRequestCommand>
{
    /// <summary>Configures the rules for <see cref="SendFriendRequestCommand"/>.</summary>
    public SendFriendRequestCommandValidator() =>
        RuleFor(command => command.AddresseeUserId)
            .NotEmpty().WithMessage("An addressee is required.");
}

/// <summary>
/// Handles <see cref="SendFriendRequestCommand"/>. It confirms the addressee exists (404 otherwise), then loads
/// every <c>Friend</c> row for the unordered pair and applies the §7.2 guard: an accepted row → no-op
/// <see cref="FriendRequestOutcome.AlreadyFriends"/>; a pending outgoing (me→them) → no-op
/// <see cref="FriendRequestOutcome.AlreadyRequested"/>; a pending incoming (them→me) →
/// <see cref="FriendRequestOutcome.ReciprocalPending"/> WITHOUT creating a second row. Only when nothing (or a
/// prior <see cref="FriendStatus.Declined"/>) remains does it create a fresh request via
/// <see cref="Friend.Request"/> (self-request → <see cref="Cinora.Domain.Exceptions.DomainException"/> → 400) and co-persist the
/// addressee's notification. A prior declined <em>outgoing</em> row is removed first so the fresh directed
/// request does not collide with the unique index (modern EF Core orders the delete before the insert).
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting requester (§2, ADR 0009).</param>
/// <param name="realtime">The realtime push port; a best-effort live push runs after a committed request notification (§9.2).</param>
/// <param name="pushDispatch">The Web Push fan-out port (ADR 0020); enqueued by the shared dispatcher after the SignalR push.</param>
/// <param name="logger">Records a Warning if the best-effort push fails (the request/notification still commit).</param>
public sealed class SendFriendRequestCommandHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    IRealtimeNotifier realtime,
    IPushDispatch pushDispatch,
    ILogger<SendFriendRequestCommandHandler> logger)
    : IRequestHandler<SendFriendRequestCommand, SendFriendRequestResult>
{
    /// <inheritdoc />
    public async Task<SendFriendRequestResult> Handle(
        SendFriendRequestCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();
        var addressee = request.AddresseeUserId;

        var addresseeExists = await db.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == addressee, cancellationToken);
        if (!addresseeExists)
        {
            throw new NotFoundException($"User ({addressee}) was not found.");
        }

        // Block enforcement (§3): a block in either direction silently prevents the request. The caller-facing
        // message is identical to a normal send, so a blocked sender cannot detect the block (§14).
        if (await BlockQueries.AreBlockedEitherWayAsync(db, me, addressee, cancellationToken))
        {
            return new SendFriendRequestResult(FriendRequestOutcome.Blocked);
        }

        // The directed-pair guard (§7.2): load every row for the unordered pair {me, addressee}, tracked so a
        // prior declined outgoing row can be removed to make way for a fresh request.
        var pairRows = await db.Friends
            .Where(friend => (friend.RequesterId == me && friend.AddresseeId == addressee)
                || (friend.RequesterId == addressee && friend.AddresseeId == me))
            .ToListAsync(cancellationToken);

        if (pairRows.Exists(row => row.Status == FriendStatus.Accepted))
        {
            return new SendFriendRequestResult(FriendRequestOutcome.AlreadyFriends);
        }

        if (pairRows.Exists(row => row.Status == FriendStatus.Pending && row.RequesterId == me))
        {
            return new SendFriendRequestResult(FriendRequestOutcome.AlreadyRequested);
        }

        if (pairRows.Exists(row => row.Status == FriendStatus.Pending && row.RequesterId == addressee))
        {
            // They already asked us — do NOT create a mirror row; the UI prompts to accept theirs instead.
            return new SendFriendRequestResult(FriendRequestOutcome.ReciprocalPending);
        }

        // Only a prior declined row (or nothing) remains. Remove a declined outgoing (me→addressee) row so the
        // fresh directed request does not trip the unique (RequesterId, AddresseeId) index.
        var priorDeclinedOutgoing = pairRows.Find(
            row => row.RequesterId == me && row.AddresseeId == addressee);
        if (priorDeclinedOutgoing is not null)
        {
            db.Friends.Remove(priorDeclinedOutgoing);
        }

        // Domain guard: a self-request throws DomainException (→ 400) before anything is staged.
        var friend = Friend.Request(me, addressee);
        db.Friends.Add(friend);

        var requesterName = await db.Users
            .AsNoTracking()
            .Where(user => user.Id == me)
            .Select(user => user.DisplayName)
            .FirstAsync(cancellationToken);

        var notification = Notification.Create(
            addressee,
            NotificationType.FriendRequest,
            $"{requesterName} sent you a friend request",
            actorUserId: me,
            targetId: friend.Id);
        db.Notifications.Add(notification);

        await db.SaveChangesAsync(cancellationToken);

        // Best-effort live push AFTER the commit — a push failure never fails the request (§9.2).
        var dto = new NotificationDto(
            notification.Id,
            notification.Type,
            notification.Message,
            requesterName,
            notification.TargetId,
            notification.CreatedAtUtc);

        await RealtimeNotificationDispatcher.DispatchBestEffortAsync(
            realtime, db, pushDispatch, logger, addressee, dto, cancellationToken);

        return new SendFriendRequestResult(FriendRequestOutcome.Created);
    }
}
