using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Entities;
using Cinora.Domain.ValueObjects;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Reviews;

/// <summary>
/// Edits an existing review's rating and body. Ownership is enforced inside the unit of work: the tracked
/// review is loaded and its <c>UserId</c> compared to the server-resolved current user — a mismatch throws
/// <see cref="ForbiddenAccessException"/> (→ 403), a missing review throws <see cref="NotFoundException"/>
/// (→ 404). A hidden edit button is never the gate; this handler is (§2, ADR 0009).
/// </summary>
/// <param name="ReviewId">The id of the review to edit (the resource; from the route).</param>
/// <param name="Rating">The new 1–10 score.</param>
/// <param name="Body">The new plain-text body (required, max <see cref="Review.BodyMaxLength"/>).</param>
public sealed record EditReviewCommand(Guid ReviewId, int Rating, string Body) : IRequest<Unit>;

/// <summary>Validates <see cref="EditReviewCommand"/> — the same rating/body bounds as create (§5.1).</summary>
public sealed class EditReviewCommandValidator : AbstractValidator<EditReviewCommand>
{
    /// <summary>Configures the rules for <see cref="EditReviewCommand"/>.</summary>
    public EditReviewCommandValidator()
    {
        RuleFor(command => command.Rating)
            .InclusiveBetween(Rating.MinValue, Rating.MaxValue)
            .WithMessage($"Rating must be between {Rating.MinValue} and {Rating.MaxValue}.");

        RuleFor(command => command.Body)
            .NotEmpty().WithMessage("A review body is required.")
            .MaximumLength(Review.BodyMaxLength)
            .WithMessage($"A review body cannot exceed {Review.BodyMaxLength} characters.");
    }
}

/// <summary>
/// Handles <see cref="EditReviewCommand"/>: load the tracked review (404 if missing), enforce ownership
/// (403 if not the author), then apply the domain mutations (<see cref="Review.ChangeRating"/> +
/// <see cref="Review.EditBody"/>, which stamp <c>UpdatedAtUtc</c> and re-guard their invariants) and save.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (§2, ADR 0009).</param>
public sealed class EditReviewCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<EditReviewCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(EditReviewCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.GetRequiredUserId();

        var review = await db.Reviews
            .FirstOrDefaultAsync(candidate => candidate.Id == request.ReviewId, cancellationToken)
            ?? throw new NotFoundException($"Review ({request.ReviewId}) was not found.");

        if (review.UserId != userId)
        {
            throw new ForbiddenAccessException("You can only edit your own review.");
        }

        review.ChangeRating(Rating.From(request.Rating));
        review.EditBody(request.Body);

        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}
