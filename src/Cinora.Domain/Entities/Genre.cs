using Cinora.Domain.Common;

namespace Cinora.Domain.Entities;

/// <summary>A TMDB genre (for example "Drama" or "Science Fiction") that titles can be tagged with.</summary>
public sealed class Genre
{
    /// <summary>The maximum allowed length of a <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    private Genre()
    {
    }

    /// <summary>Cinora's internal identifier for the genre.</summary>
    public Guid Id { get; private set; }

    /// <summary>The TMDB genre identifier this record was sourced from.</summary>
    public int TmdbGenreId { get; private set; }

    /// <summary>The display name of the genre.</summary>
    public string Name { get; private set; } = null!;

    /// <summary>Creates a genre from TMDB metadata.</summary>
    /// <param name="tmdbGenreId">The TMDB genre identifier; must be greater than zero.</param>
    /// <param name="name">The display name; required, max <see cref="NameMaxLength"/> characters.</param>
    /// <returns>A new <see cref="Genre"/>.</returns>
    public static Genre Create(int tmdbGenreId, string name) =>
        new()
        {
            Id = Guid.NewGuid(),
            TmdbGenreId = Guard.Positive(tmdbGenreId, nameof(tmdbGenreId)),
            Name = Guard.Required(name, NameMaxLength, nameof(name)),
        };
}
