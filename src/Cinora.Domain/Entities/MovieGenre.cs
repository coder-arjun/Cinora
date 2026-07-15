namespace Cinora.Domain.Entities;

/// <summary>
/// The many-to-many join between a <see cref="Movie"/> and a <see cref="Genre"/>. It has no
/// surrogate key; the pair (<see cref="MovieId"/>, <see cref="GenreId"/>) is the composite key,
/// configured by EF Core in Milestone 1.2.
/// </summary>
public sealed class MovieGenre
{
    private MovieGenre()
    {
    }

    /// <summary>The identifier of the linked <see cref="Movie"/>.</summary>
    public Guid MovieId { get; private set; }

    /// <summary>The identifier of the linked <see cref="Genre"/>.</summary>
    public Guid GenreId { get; private set; }

    /// <summary>Links a movie to a genre.</summary>
    /// <param name="movieId">The movie identifier.</param>
    /// <param name="genreId">The genre identifier.</param>
    /// <returns>A new <see cref="MovieGenre"/> join record.</returns>
    public static MovieGenre Link(Guid movieId, Guid genreId) =>
        new()
        {
            MovieId = movieId,
            GenreId = genreId,
        };
}
