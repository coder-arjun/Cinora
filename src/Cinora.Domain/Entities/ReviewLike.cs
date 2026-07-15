namespace Cinora.Domain.Entities;

/// <summary>
/// Records that a user liked a review. It has no surrogate key; the pair
/// (<see cref="ReviewId"/>, <see cref="UserId"/>) is the composite key, configured by EF Core in
/// Milestone 1.2, which also guarantees a user can like a review at most once.
/// </summary>
public sealed class ReviewLike
{
    private ReviewLike()
    {
    }

    /// <summary>The identifier of the liked <see cref="Review"/>.</summary>
    public Guid ReviewId { get; private set; }

    /// <summary>The identifier of the user who liked the review.</summary>
    public Guid UserId { get; private set; }

    /// <summary>The UTC instant the like was recorded.</summary>
    public DateTime LikedAtUtc { get; private set; }

    /// <summary>Records a like of a review by a user.</summary>
    /// <param name="reviewId">The liked review's identifier.</param>
    /// <param name="userId">The liking user's identifier.</param>
    /// <returns>A new <see cref="ReviewLike"/>.</returns>
    public static ReviewLike Create(Guid reviewId, Guid userId) =>
        new()
        {
            ReviewId = reviewId,
            UserId = userId,
            LikedAtUtc = DateTime.UtcNow,
        };
}
