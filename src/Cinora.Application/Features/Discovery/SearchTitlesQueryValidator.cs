using FluentValidation;

namespace Cinora.Application.Features.Discovery;

/// <summary>
/// Validates <see cref="SearchTitlesQuery"/> — the phase's FIRST real validator, auto-discovered by the
/// existing <c>AddValidatorsFromAssembly(..., includeInternalTypes: true)</c> registration and enforced by
/// the wired <c>ValidationBehavior</c> (which throws the Application <c>ValidationException</c>, mapped by the
/// <c>GlobalExceptionHandler</c> to a 400 <c>ValidationProblemDetails</c>). The query is the contract floor
/// (non-empty, ≤ 100 chars); the presentation-layer <c>MinQueryLength = 2</c> threshold is layered on top in
/// the controller/Alpine, not here.
/// </summary>
public sealed class SearchTitlesQueryValidator : AbstractValidator<SearchTitlesQuery>
{
    /// <summary>Configures the rules for <see cref="SearchTitlesQuery"/>.</summary>
    public SearchTitlesQueryValidator()
    {
        RuleFor(query => query.Query)
            .NotEmpty().WithMessage("Enter something to search for.")
            .MaximumLength(100).WithMessage("Search is limited to 100 characters.");

        RuleFor(query => query.Page)
            .GreaterThanOrEqualTo(1);
    }
}
