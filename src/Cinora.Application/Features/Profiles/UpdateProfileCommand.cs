using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Entities;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Profiles;

/// <summary>
/// Updates the CURRENT user's own profile — display name + privacy (public / friends-only). The actor is the
/// resource owner, resolved server-side from <see cref="ICurrentUser"/>; the command binds NO user id (you can
/// only edit yourself), so there is no cross-user edit surface and no <c>403</c> ownership arm (ADR 0009, §4).
/// The authoritative name guard stays on the domain (<see cref="User.Rename"/> → <c>Guard.Required</c>); the
/// validator is the friendly 400.
/// </summary>
/// <param name="DisplayName">The new display name; required, max <see cref="User.DisplayNameMaxLength"/>.</param>
/// <param name="IsProfilePublic">The new visibility: <c>true</c> = public, <c>false</c> = friends-only (§4.3).</param>
/// <param name="DefaultLanguage">The preferred movie language (validated lower-case ISO-639-1), or <c>null</c> for Global.</param>
public sealed record UpdateProfileCommand(string DisplayName, bool IsProfilePublic, string? DefaultLanguage = null) : IRequest<Unit>;

/// <summary>
/// Validates <see cref="UpdateProfileCommand"/> for a friendly 400: the display name is present (not blank) and
/// within the domain length bound. Mirrors the authoritative <c>User.Rename</c> / <c>Guard.Required</c> guard
/// (required + max length on the raw value) so the validator never lets through a value the domain rejects, nor
/// rejects one it accepts.
/// </summary>
public sealed class UpdateProfileCommandValidator : AbstractValidator<UpdateProfileCommand>
{
    /// <summary>Configures the rules for <see cref="UpdateProfileCommand"/>.</summary>
    public UpdateProfileCommandValidator()
    {
        RuleFor(command => command.DisplayName)
            .NotEmpty().WithMessage("A display name is required.")
            .MaximumLength(User.DisplayNameMaxLength)
            .WithMessage($"A display name cannot exceed {User.DisplayNameMaxLength} characters.");
    }
}

/// <summary>
/// Handles <see cref="UpdateProfileCommand"/>: load the current user's tracked <see cref="User"/> aggregate,
/// apply the domain mutations (<see cref="User.Rename"/> + <see cref="User.SetProfileVisibility"/>) and save.
/// The invariants live on the domain; this handler stays a thin orchestrator (no business rules).
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (§2, ADR 0009).</param>
public sealed class UpdateProfileCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<UpdateProfileCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(UpdateProfileCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.GetRequiredUserId();

        var user = await db.Users
            .FirstOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken)
            ?? throw new NotFoundException($"User ({userId}) was not found.");

        // Display names are globally UNIQUE (a case-insensitive login handle since the login-by-name change), so a
        // rename to a name ANOTHER user already holds must fail with a friendly 400 rather than 500 on the unique
        // index at SaveChanges. Pre-check the normalized handle excluding self; the unique index is still the
        // ultimate guarantee for the rare concurrent race. (SqlException translation stays in Infrastructure — the
        // dependency rule bars referencing the SQL provider here — so the realistic case is covered by this check.)
        var normalized = User.Normalize(request.DisplayName);
        var takenByAnother = await db.Users
            .AsNoTracking()
            .AnyAsync(candidate => candidate.NormalizedDisplayName == normalized && candidate.Id != userId, cancellationToken);
        if (takenByAnother)
        {
            // Fully qualified: the app's own Common.Exceptions.ValidationException (mapped to 400 by the global
            // handler), not FluentValidation.ValidationException — both are in scope here (validator + failures).
            throw new Cinora.Application.Common.Exceptions.ValidationException(
            [
                new ValidationFailure(
                    nameof(UpdateProfileCommand.DisplayName),
                    "That display name is already taken. Please choose a different one."),
            ]);
        }

        user.Rename(request.DisplayName);
        user.SetProfileVisibility(request.IsProfilePublic);
        user.SetDefaultLanguage(request.DefaultLanguage);

        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}
