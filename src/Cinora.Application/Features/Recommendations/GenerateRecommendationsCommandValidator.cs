using FluentValidation;

namespace Cinora.Application.Features.Recommendations;

/// <summary>
/// Validates <see cref="GenerateRecommendationsCommand"/> — auto-discovered by <c>AddApplication</c>'s
/// <c>AddValidatorsFromAssembly(..., includeInternalTypes: true)</c> scan and enforced by the wired
/// <c>ValidationBehavior</c>. The command targets exactly one user (the precompute job supplies the id), so the
/// contract floor is a non-empty <see cref="GenerateRecommendationsCommand.UserId"/>; the house rule that every
/// command carries a validator keeps a defaulted <see cref="System.Guid.Empty"/> from reaching the handler.
/// </summary>
public sealed class GenerateRecommendationsCommandValidator : AbstractValidator<GenerateRecommendationsCommand>
{
    /// <summary>Configures the rules for <see cref="GenerateRecommendationsCommand"/>.</summary>
    public GenerateRecommendationsCommandValidator()
    {
        RuleFor(command => command.UserId)
            .NotEmpty();
    }
}
