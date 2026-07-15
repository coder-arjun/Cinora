namespace Cinora.Application.Common.Tmdb;

/// <summary>
/// An Application-owned read model for a TMDB genre (for example "Drama"). Part of the
/// <see cref="Interfaces.ITmdbClient"/> contract; TMDB's wire DTOs are mapped to this shape inside
/// Infrastructure and never leak outward (ADR 0006). The catalog uses <see cref="TmdbGenreId"/> to
/// resolve the numeric genre ids carried on rail and search cards to display names.
/// </summary>
public sealed record TmdbGenre
{
    /// <summary>The TMDB genre identifier.</summary>
    public required int TmdbGenreId { get; init; }

    /// <summary>The display name of the genre.</summary>
    public required string Name { get; init; }
}
