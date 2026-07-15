using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.ValueObjects;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Notifications;

/// <summary>
/// Replaces the CURRENT user's per-type Web Push notification preferences (Milestone 6.3, §5.3). The actor is the
/// resource owner, resolved server-side from <see cref="ICurrentUser"/>; the command binds NO user id (you can
/// only edit your own), so there is no cross-user edit surface. Push only — the toggles never gate the in-app
/// inbox row or the SignalR toast, which always arrive (ADR 0020 §5.2).
/// </summary>
/// <param name="PushFriendRequests">Whether friend-request events should push.</param>
/// <param name="PushFriendAccepted">Whether friend-accepted events should push.</param>
/// <param name="PushReviewLikes">Whether review-like events should push.</param>
/// <param name="PushComments">Whether comment events should push.</param>
public sealed record UpdateNotificationPreferencesCommand(
    bool PushFriendRequests,
    bool PushFriendAccepted,
    bool PushReviewLikes,
    bool PushComments) : IRequest<Unit>;

/// <summary>
/// Validates <see cref="UpdateNotificationPreferencesCommand"/>. The four fields are booleans (no bounds to
/// enforce); this validator exists for pipeline consistency and to give the command an explicit, assembly-scanned
/// validation seam should richer rules arrive. Owner-scoping (no cross-user edit) is enforced in the handler via
/// <see cref="ICurrentUser"/>, not here.
/// </summary>
public sealed class UpdateNotificationPreferencesCommandValidator
    : AbstractValidator<UpdateNotificationPreferencesCommand>
{
    /// <summary>Configures the (currently rule-free) validation for the command.</summary>
    public UpdateNotificationPreferencesCommandValidator()
    {
    }
}

/// <summary>
/// Handles <see cref="UpdateNotificationPreferencesCommand"/>: load the current user's tracked <c>User</c>
/// aggregate, apply the domain mutation (<c>User.UpdateNotificationPreferences</c> with a fresh
/// <see cref="NotificationPreferences"/>), and save. A thin orchestrator — the value object is the invariant.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (ADR 0009).</param>
public sealed class UpdateNotificationPreferencesCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<UpdateNotificationPreferencesCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(
        UpdateNotificationPreferencesCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.GetRequiredUserId();

        var user = await db.Users
            .FirstOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken)
            ?? throw new NotFoundException($"User ({userId}) was not found.");

        user.UpdateNotificationPreferences(new NotificationPreferences(
            request.PushFriendRequests,
            request.PushFriendAccepted,
            request.PushReviewLikes,
            request.PushComments));

        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}
