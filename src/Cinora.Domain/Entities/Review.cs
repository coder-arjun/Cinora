using Cinora.Domain.Common;
using Cinora.Domain.Exceptions;
using Cinora.Domain.ValueObjects;

namespace Cinora.Domain.Entities;

/// <summary>
/// A user's review of a title: a <see cref="Rating"/> on the 1–10 scale plus a written body.
/// The aggregate guards a non-empty, bounded body and records when it was last edited. A user may
/// hold at most one review per title — that uniqueness is enforced by EF Core in Milestone 1.2.
/// </summary>
public sealed class Review
{
    /// <summary>The maximum allowed length of a review <see cref="Body"/>.</summary>
    public const int BodyMaxLength = 4000;

    private Review()
    {
    }

    /// <summary>The unique identifier of the review.</summary>
    public Guid Id { get; private set; }

    /// <summary>The identifier of the author.</summary>
    public Guid UserId { get; private set; }

    /// <summary>The identifier of the reviewed title.</summary>
    public Guid MovieId { get; private set; }

    /// <summary>The 1–10 score.</summary>
    public Rating Rating { get; private set; }

    /// <summary>The review text.</summary>
    public string Body { get; private set; } = null!;

    /// <summary>The UTC instant the review was created.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>The UTC instant the review was last edited, or <c>null</c> if never edited.</summary>
    public DateTime? UpdatedAtUtc { get; private set; }

    /// <summary>Creates a new review.</summary>
    /// <param name="userId">The author's identifier.</param>
    /// <param name="movieId">The reviewed title's identifier.</param>
    /// <param name="rating">The score; must be a valid 1–10 <see cref="Rating"/> (the struct default is rejected).</param>
    /// <param name="body">The review text; required, max <see cref="BodyMaxLength"/> characters.</param>
    /// <returns>A new <see cref="Review"/>.</returns>
    /// <exception cref="DomainException">Thrown when <paramref name="rating"/> is invalid or <paramref name="body"/> is missing or too long.</exception>
    public static Review Create(Guid userId, Guid movieId, Rating rating, string body)
    {
        if (!rating.IsValid)
        {
            throw new DomainException("Rating must be between 1 and 10.");
        }

        return new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MovieId = movieId,
            Rating = rating,
            Body = Guard.Required(body, BodyMaxLength, nameof(body)),
            CreatedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>Changes the review's score and marks it edited.</summary>
    /// <param name="rating">The new score; must be a valid 1–10 <see cref="Rating"/> (the struct default is rejected).</param>
    /// <exception cref="DomainException">Thrown when <paramref name="rating"/> is invalid.</exception>
    public void ChangeRating(Rating rating)
    {
        if (!rating.IsValid)
        {
            throw new DomainException("Rating must be between 1 and 10.");
        }

        Rating = rating;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Replaces the review text and marks it edited.</summary>
    /// <param name="body">The new review text; required, max <see cref="BodyMaxLength"/> characters.</param>
    public void EditBody(string body)
    {
        Body = Guard.Required(body, BodyMaxLength, nameof(body));
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
