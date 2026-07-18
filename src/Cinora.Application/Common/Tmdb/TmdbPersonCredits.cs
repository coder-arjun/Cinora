namespace Cinora.Application.Common.Tmdb;

/// <summary>
/// An Application-owned read model for a TMDB person (an actor) and their acting filmography — the data
/// behind the "cast member → their titles" page (feature #9). Like every other <see cref="Interfaces.ITmdbClient"/>
/// read model, TMDB's wire DTOs are mapped to this shape inside Infrastructure and never leak outward (ADR 0006).
/// <see cref="ProfilePath"/> carries the raw TMDB image path; the view builds the URL and picks a size.
/// </summary>
public sealed record TmdbPersonCredits
{
    /// <summary>The TMDB person identifier.</summary>
    public required int PersonId { get; init; }

    /// <summary>The person's display name.</summary>
    public required string Name { get; init; }

    /// <summary>The raw TMDB profile image path, or <c>null</c> when none exists.</summary>
    public string? ProfilePath { get; init; }

    /// <summary>
    /// The person's acting credits as browsable title summaries (movies and series), unranked and
    /// unfiltered; the ranking/filtering to the "most famous" titles is a query concern, not the port's.
    /// </summary>
    public IReadOnlyList<TmdbTitleSummary> Titles { get; init; } = [];
}
