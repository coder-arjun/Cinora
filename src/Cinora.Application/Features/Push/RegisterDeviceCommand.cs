using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Entities;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Push;

/// <summary>
/// Registers (or refreshes) a Web Push subscription for the current user's device (Milestone 6.2, ADR 0020
/// §3.3). The acting user is server-resolved from <see cref="ICurrentUser"/> (ADR 0009 — <b>no</b> user id is
/// bound from the client); the command carries only the browser subscription transport fields. It is an
/// <b>upsert</b> keyed by endpoint: a re-subscribe with the same endpoint rotates the keys on the existing
/// <see cref="Device"/> (and marks it seen) rather than inserting a duplicate — one subscription per Device,
/// many Devices per user (phone + desktop). Milestone 6.2 matches the endpoint <b>in memory</b> over the user's
/// small device set (via the existing <c>(UserId)</c> index); the persisted <c>EndpointHash</c> + unique index
/// for a race-safe upsert is Milestone 6.3's additive migration.
/// </summary>
/// <param name="Endpoint">The push service endpoint (from the browser <c>PushSubscription</c>).</param>
/// <param name="P256dh">The client's P-256 ECDH public key (base64url).</param>
/// <param name="Auth">The client's authentication secret (base64url).</param>
public sealed record RegisterDeviceCommand(string Endpoint, string P256dh, string Auth) : IRequest<Unit>;

/// <summary>
/// Validates <see cref="RegisterDeviceCommand"/>: the endpoint and both encryption keys must be present and within
/// the persisted <see cref="Device"/> column bounds (6.2 security Low) — an oversized subscription is a friendly
/// <c>400</c> here rather than a <c>SqlException</c> truncation 500 downstream.
/// </summary>
public sealed class RegisterDeviceCommandValidator : AbstractValidator<RegisterDeviceCommand>
{
    /// <summary>The maximum endpoint length (matches the <see cref="Device"/> <c>Endpoint</c> column bound).</summary>
    public const int EndpointMaxLength = 2048;

    /// <summary>The maximum push-key length (matches the <see cref="Device"/> key column bounds).</summary>
    public const int KeyMaxLength = 256;

    /// <summary>Configures the rules for <see cref="RegisterDeviceCommand"/>.</summary>
    public RegisterDeviceCommandValidator()
    {
        RuleFor(command => command.Endpoint)
            .NotEmpty().WithMessage("A push endpoint is required.")
            .MaximumLength(EndpointMaxLength);
        RuleFor(command => command.P256dh)
            .NotEmpty().WithMessage("A push public key is required.")
            .MaximumLength(KeyMaxLength);
        RuleFor(command => command.Auth)
            .NotEmpty().WithMessage("A push auth secret is required.")
            .MaximumLength(KeyMaxLength);
    }
}

/// <summary>
/// Handles <see cref="RegisterDeviceCommand"/>: resolve the current user server-side, then upsert keyed on the
/// indexed, race-safe <c>(UserId, EndpointHash)</c> (Milestone 6.3) — refresh the keys of a matching device or add
/// a new one. If a concurrent subscribe won the race and tripped the unique index, the resulting
/// <see cref="DbUpdateException"/> is reconciled to the same idempotent upsert (refresh the row that landed).
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (ADR 0009).</param>
public sealed class RegisterDeviceCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<RegisterDeviceCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(RegisterDeviceCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.GetRequiredUserId();
        var endpointHash = Device.ComputeEndpointHash(request.Endpoint);

        // Match on the indexed (UserId, EndpointHash) — the fixed-width hash IS indexable where the raw endpoint
        // (>900-byte key) is not. A re-subscribe rotates the keys on the existing row rather than duplicating.
        var existing = await db.Devices
            .FirstOrDefaultAsync(
                device => device.UserId == userId && device.EndpointHash == endpointHash, cancellationToken);

        if (existing is not null)
        {
            existing.UpdateSubscription(request.P256dh, request.Auth);
            await db.SaveChangesAsync(cancellationToken);
            return Unit.Value;
        }

        db.Devices.Add(Device.Register(userId, request.Endpoint, request.P256dh, request.Auth));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent subscribe for the SAME (UserId, EndpointHash) landed first and tripped the unique index.
            // Discard our now-duplicate insert and refresh the row that won the race — the same idempotent upsert.
            db.DiscardPendingChanges();

            var raced = await db.Devices
                .FirstOrDefaultAsync(
                    device => device.UserId == userId && device.EndpointHash == endpointHash, cancellationToken);

            if (raced is null)
            {
                throw; // not the expected unique-index race — surface the real fault.
            }

            raced.UpdateSubscription(request.P256dh, request.Auth);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Unit.Value;
    }
}
