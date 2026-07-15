using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Push;

/// <summary>
/// Removes a Web Push subscription for the current user's device (Milestone 6.2, ADR 0020) — the explicit
/// unsubscribe path (the browser also drops a subscription on its own; dead endpoints are separately pruned on a
/// 404/410 send). The acting user is server-resolved (ADR 0009 — no id from the client); only that user's
/// matching device is deleted, so there is no cross-user delete path. Idempotent: no matching device is a no-op.
/// </summary>
/// <param name="Endpoint">The push service endpoint of the device to remove.</param>
public sealed record UnregisterDeviceCommand(string Endpoint) : IRequest<Unit>;

/// <summary>
/// Validates <see cref="UnregisterDeviceCommand"/>: the endpoint must be present and within the persisted
/// <see cref="Cinora.Domain.Entities.Device"/> <c>Endpoint</c> column bound (6.2 security Low) — an oversized
/// value is a friendly <c>400</c> rather than a truncation fault downstream.
/// </summary>
public sealed class UnregisterDeviceCommandValidator : AbstractValidator<UnregisterDeviceCommand>
{
    /// <summary>Configures the rules for <see cref="UnregisterDeviceCommand"/>.</summary>
    public UnregisterDeviceCommandValidator() =>
        RuleFor(command => command.Endpoint)
            .NotEmpty().WithMessage("A push endpoint is required.")
            .MaximumLength(RegisterDeviceCommandValidator.EndpointMaxLength);
}

/// <summary>
/// Handles <see cref="UnregisterDeviceCommand"/>: delete the current user's device(s) matching the endpoint, if
/// any, in one <c>SaveChanges</c>. Scoped to the server-resolved user so a caller can only remove their own.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (ADR 0009).</param>
public sealed class UnregisterDeviceCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<UnregisterDeviceCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(UnregisterDeviceCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.GetRequiredUserId();

        var matches = await db.Devices
            .Where(device => device.UserId == userId && device.Endpoint == request.Endpoint)
            .ToListAsync(cancellationToken);

        if (matches.Count == 0)
        {
            return Unit.Value; // idempotent no-op
        }

        db.Devices.RemoveRange(matches);
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}
