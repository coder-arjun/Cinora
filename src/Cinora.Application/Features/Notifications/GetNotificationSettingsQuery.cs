using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Notifications;

/// <summary>
/// Loads the CURRENT user's per-type Web Push notification preferences to pre-fill the
/// <c>/settings/notifications</c> toggles (Milestone 6.3, §5.3). Owner-only: the user is resolved server-side from
/// <see cref="ICurrentUser"/> and NO id is bound from the client (ADR 0009), so there is no cross-user read.
/// </summary>
public sealed record GetNotificationSettingsQuery : IRequest<NotificationSettingsVm>;

/// <summary>The current user's per-type push preferences (default-on; the toggles govern push, not in-app).</summary>
/// <param name="PushFriendRequests">Whether friend-request events push to this user's devices.</param>
/// <param name="PushFriendAccepted">Whether friend-accepted events push to this user's devices.</param>
/// <param name="PushReviewLikes">Whether review-like events push to this user's devices.</param>
/// <param name="PushComments">Whether comment events push to this user's devices.</param>
public sealed record NotificationSettingsVm(
    bool PushFriendRequests,
    bool PushFriendAccepted,
    bool PushReviewLikes,
    bool PushComments);

/// <summary>
/// Handles <see cref="GetNotificationSettingsQuery"/> with a single <c>AsNoTracking</c> projection of the current
/// user's owned preference columns. A missing row (impossible behind <c>[Authorize]</c> given atomic registration)
/// is a defensive 404.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (ADR 0009).</param>
public sealed class GetNotificationSettingsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetNotificationSettingsQuery, NotificationSettingsVm>
{
    /// <inheritdoc />
    public async Task<NotificationSettingsVm> Handle(
        GetNotificationSettingsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.GetRequiredUserId();

        return await db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new NotificationSettingsVm(
                user.Preferences.PushFriendRequests,
                user.Preferences.PushFriendAccepted,
                user.Preferences.PushReviewLikes,
                user.Preferences.PushComments))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"User ({userId}) was not found.");
    }
}
