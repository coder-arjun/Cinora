using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Notifications;

/// <summary>
/// Returns the CURRENT user's unread notification count (§9.3): the no-JS badge fallback and the initial badge
/// seed. Recipient-scoped; uses the existing <c>(RecipientUserId, IsRead)</c> index. A single bounded count, no
/// materialization.
/// </summary>
public sealed record GetUnreadCountQuery : IRequest<int>;

/// <summary>Handles <see cref="GetUnreadCountQuery"/>: one recipient-scoped <c>CountAsync</c> over unread rows.</summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved recipient (§2, ADR 0009).</param>
public sealed class GetUnreadCountQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetUnreadCountQuery, int>
{
    /// <inheritdoc />
    public async Task<int> Handle(GetUnreadCountQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();

        return await db.Notifications
            .AsNoTracking()
            .CountAsync(
                notification => notification.RecipientUserId == me && !notification.IsRead,
                cancellationToken);
    }
}
