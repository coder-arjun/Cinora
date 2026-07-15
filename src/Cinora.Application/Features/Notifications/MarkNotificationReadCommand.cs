using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Notifications;

/// <summary>
/// Marks one of the CURRENT user's notifications as read (§9.3). Ownership is enforced by loading the row
/// scoped to the recipient: only the recipient may mark theirs, so a notification that does not exist OR belongs
/// to someone else resolves to <see cref="NotFoundException"/> (→ 404) — there is no cross-user mutation, and
/// 404 (rather than 403) leaks nothing about another user's notifications (N5, §2). The acting user is
/// server-resolved (ADR 0009).
/// </summary>
/// <param name="Id">The id of the notification to mark read (the resource; from the route).</param>
public sealed record MarkNotificationReadCommand(Guid Id) : IRequest<Unit>;

/// <summary>
/// Handles <see cref="MarkNotificationReadCommand"/>: load the tracked notification scoped to the current
/// recipient (a missing OR non-owned id → 404), mark it read (idempotent — an already-read row saves nothing),
/// and save.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved recipient (§2, ADR 0009).</param>
public sealed class MarkNotificationReadCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<MarkNotificationReadCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(MarkNotificationReadCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();

        // Scope the load to the recipient: a non-owned (or missing) id simply is not found — no cross-user read
        // or mutation, and the 404 is indistinguishable from a truly missing row (no information leak).
        var notification = await db.Notifications
            .FirstOrDefaultAsync(
                candidate => candidate.Id == request.Id && candidate.RecipientUserId == me,
                cancellationToken)
            ?? throw new NotFoundException($"Notification ({request.Id}) was not found.");

        if (!notification.IsRead)
        {
            notification.MarkRead();
            await db.SaveChangesAsync(cancellationToken);
        }

        return Unit.Value;
    }
}
