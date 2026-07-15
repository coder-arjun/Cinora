using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Entities;
using Cinora.Domain.ValueObjects;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Reviews;

/// <summary>
/// Creates the current user's review of a title (rating 1–10 + plain-text body). The acting user is
/// resolved server-side from <see cref="ICurrentUser"/> — the command carries only the internal
/// <paramref name="MovieId"/> (the resource), never an author id (ADR 0009, §2). A user may hold at most one
/// review per title; a second attempt (whether sequential or a concurrent race on the unique
/// <c>(UserId, MovieId)</c> index) resolves to the existing review rather than a duplicate or an error (§5.1).
/// </summary>
/// <param name="MovieId">The internal <see cref="Movie.Id"/> of the reviewed title (resolved by the caller via
/// <c>EnsureTitleCachedCommand</c> — §4).</param>
/// <param name="Rating">The 1–10 score. The validator rejects out-of-range for a friendly 400; the domain
/// <see cref="Cinora.Domain.ValueObjects.Rating"/> is the authoritative guard.</param>
/// <param name="Body">The plain-text review body (required, max <see cref="Review.BodyMaxLength"/>).</param>
public sealed record CreateReviewCommand(Guid MovieId, int Rating, string Body)
    : IRequest<CreateReviewResult>;

/// <summary>The outcome of a <see cref="CreateReviewCommand"/>.</summary>
/// <param name="ReviewId">The id of the created review, or of the user's pre-existing review when
/// <paramref name="AlreadyExisted"/> is <c>true</c>.</param>
/// <param name="AlreadyExisted">Whether the user had already reviewed this title (no new row was created).</param>
public sealed record CreateReviewResult(Guid ReviewId, bool AlreadyExisted);

/// <summary>
/// Validates <see cref="CreateReviewCommand"/> for a friendly 400: the rating is within 1–10 and the body is
/// present and within the domain length bound. This is defense-in-depth, not the XSS boundary (§3) — output
/// encoding is; and it does not re-implement the rating invariant, which stays authoritative in the domain.
/// </summary>
public sealed class CreateReviewCommandValidator : AbstractValidator<CreateReviewCommand>
{
    /// <summary>Configures the rules for <see cref="CreateReviewCommand"/>.</summary>
    public CreateReviewCommandValidator()
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
/// Handles <see cref="CreateReviewCommand"/>. It first probes the unique <c>(UserId, MovieId)</c> index for an
/// existing review (the common "already reviewed" case, resolved without an exception); otherwise it creates
/// the review via <see cref="Review.Create"/> and saves. A concurrent insert that trips the unique index is
/// caught, the staged insert is discarded, and the winning row is re-probed and returned as already-existed —
/// the same reconcile-on-race pattern the catalog seam uses (ADR 0008). Any other
/// <see cref="DbUpdateException"/> (e.g. a bad <c>MovieId</c> foreign key) is not a review race and is
/// rethrown to surface as a fault.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (§2, ADR 0009).</param>
public sealed class CreateReviewCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<CreateReviewCommand, CreateReviewResult>
{
    /// <inheritdoc />
    public async Task<CreateReviewResult> Handle(CreateReviewCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.GetRequiredUserId();

        var existingId = await FindExistingReviewIdAsync(userId, request.MovieId, cancellationToken);
        if (existingId is not null)
        {
            return new CreateReviewResult(existingId.Value, AlreadyExisted: true);
        }

        var review = Review.Create(userId, request.MovieId, Rating.From(request.Rating), request.Body);
        db.Reviews.Add(review);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new CreateReviewResult(review.Id, AlreadyExisted: false);
        }
        catch (DbUpdateException)
        {
            // Lost a race on the unique (UserId, MovieId) index. Discard our now-conflicting Added row so it
            // cannot re-flush, then reconcile to the winner. If nothing resolves, the failure was NOT a review
            // race (e.g. a FK violation) — rethrow so it surfaces rather than masking a real fault.
            db.DiscardPendingChanges();

            var racedId = await FindExistingReviewIdAsync(userId, request.MovieId, cancellationToken);
            if (racedId is not null)
            {
                return new CreateReviewResult(racedId.Value, AlreadyExisted: true);
            }

            throw;
        }
    }

    private Task<Guid?> FindExistingReviewIdAsync(Guid userId, Guid movieId, CancellationToken cancellationToken) =>
        db.Reviews
            .AsNoTracking()
            .Where(review => review.UserId == userId && review.MovieId == movieId)
            .Select(review => (Guid?)review.Id)
            .FirstOrDefaultAsync(cancellationToken);
}
