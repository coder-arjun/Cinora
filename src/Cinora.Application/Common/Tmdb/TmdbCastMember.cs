namespace Cinora.Application.Common.Tmdb;

/// <summary>
/// An Application-owned read model for a single cast member of a TMDB title, sourced from the credits
/// returned alongside <see cref="TmdbTitleDetails"/>. Part of the <see cref="Interfaces.ITmdbClient"/>
/// contract; TMDB's wire DTOs are mapped to this shape inside Infrastructure and never leak outward
/// (ADR 0006). <see cref="ProfilePath"/> is a raw TMDB path; the view builds a sized URL from it.
/// </summary>
public sealed record TmdbCastMember
{
    /// <summary>The TMDB person identifier of the actor.</summary>
    public required int TmdbId { get; init; }

    /// <summary>The actor's name.</summary>
    public required string Name { get; init; }

    /// <summary>The character portrayed, or <c>null</c> when TMDB provides none.</summary>
    public string? Character { get; init; }

    /// <summary>The raw TMDB profile image path, or <c>null</c> when none exists.</summary>
    public string? ProfilePath { get; init; }

    /// <summary>
    /// The billing order (ascending; 0 is top-billed), used to rank the cast. An unknown billing is stored
    /// as <see cref="int.MaxValue"/> so it sorts last on any later re-sort by this value.
    /// </summary>
    public int Order { get; init; }
}
