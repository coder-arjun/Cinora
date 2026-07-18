namespace Cinora.Domain.Entities;

/// <summary>
/// Records that a user "loves" a title (a movie/series-level like, distinct from a <see cref="Review"/>). There is
/// no surrogate key; the pair (<see cref="MovieId"/>, <see cref="UserId"/>) is the composite key, which guarantees
/// a user can love a title at most once. Drives the poster love button + count and the "most loved" sort.
/// </summary>
public sealed class MovieLike
{
    private MovieLike()
    {
    }

    /// <summary>The identifier of the loved <see cref="Movie"/> (internal id, not the TMDB id).</summary>
    public Guid MovieId { get; private set; }

    /// <summary>The identifier of the user who loved the title.</summary>
    public Guid UserId { get; private set; }

    /// <summary>The UTC instant the love was recorded.</summary>
    public DateTime LikedAtUtc { get; private set; }

    /// <summary>Records that a user loves a title.</summary>
    /// <param name="movieId">The loved title's internal identifier.</param>
    /// <param name="userId">The loving user's identifier.</param>
    /// <returns>A new <see cref="MovieLike"/>.</returns>
    public static MovieLike Create(Guid movieId, Guid userId) =>
        new()
        {
            MovieId = movieId,
            UserId = userId,
            LikedAtUtc = DateTime.UtcNow,
        };
}
