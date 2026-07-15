using Cinora.Domain.Common;

namespace Cinora.Domain.Entities;

/// <summary>A user's comment on a <see cref="Review"/>. Guards a non-empty, bounded body.</summary>
public sealed class Comment
{
    /// <summary>The maximum allowed length of a comment <see cref="Body"/>.</summary>
    public const int BodyMaxLength = 2000;

    private Comment()
    {
    }

    /// <summary>The unique identifier of the comment.</summary>
    public Guid Id { get; private set; }

    /// <summary>The identifier of the commented-on <see cref="Review"/>.</summary>
    public Guid ReviewId { get; private set; }

    /// <summary>The identifier of the comment's author.</summary>
    public Guid UserId { get; private set; }

    /// <summary>The comment text.</summary>
    public string Body { get; private set; } = null!;

    /// <summary>The UTC instant the comment was created.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Creates a new comment on a review.</summary>
    /// <param name="reviewId">The commented-on review's identifier.</param>
    /// <param name="userId">The author's identifier.</param>
    /// <param name="body">The comment text; required, max <see cref="BodyMaxLength"/> characters.</param>
    /// <returns>A new <see cref="Comment"/>.</returns>
    public static Comment Create(Guid reviewId, Guid userId, string body) =>
        new()
        {
            Id = Guid.NewGuid(),
            ReviewId = reviewId,
            UserId = userId,
            Body = Guard.Required(body, BodyMaxLength, nameof(body)),
            CreatedAtUtc = DateTime.UtcNow,
        };
}
