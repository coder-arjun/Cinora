using Cinora.Domain.Enums;

namespace Cinora.Application.Common.Tmdb;

/// <summary>
/// An Application-owned read model summarizing a single TMDB title (movie or series) as it appears in a
/// trending, popular, top-rated, or search list. This is part of the <see cref="Interfaces.ITmdbClient"/>
/// contract: TMDB's snake_case wire DTOs are mapped to this shape inside Infrastructure and never leak
/// outward (ADR 0006). Image fields carry the raw TMDB path (for example <c>/abc.jpg</c>); the view builds
/// a full URL and picks a size via <see cref="Interfaces.ITmdbImageUrlBuilder"/>. Reusing the Domain
/// <see cref="MediaType"/> enum is deliberate (Application → Domain is allowed) and avoids a duplicate.
/// </summary>
public sealed record TmdbTitleSummary
{
    /// <summary>The TMDB identifier of the title.</summary>
    public required int TmdbId { get; init; }

    /// <summary>Whether the title is a movie or a series.</summary>
    public required MediaType MediaType { get; init; }

    /// <summary>The display title (TMDB <c>title</c> for movies, <c>name</c> for series).</summary>
    public required string Title { get; init; }

    /// <summary>The synopsis, or <c>null</c> when TMDB provides none.</summary>
    public string? Overview { get; init; }

    /// <summary>The raw TMDB poster image path, or <c>null</c> when none exists.</summary>
    public string? PosterPath { get; init; }

    /// <summary>The raw TMDB backdrop image path, or <c>null</c> when none exists.</summary>
    public string? BackdropPath { get; init; }

    /// <summary>The release date (TMDB <c>release_date</c> or <c>first_air_date</c>), or <c>null</c> when unknown.</summary>
    public DateOnly? ReleaseDate { get; init; }

    /// <summary>The TMDB community vote average on a 0–10 scale.</summary>
    public double VoteAverage { get; init; }

    /// <summary>The number of TMDB community votes backing <see cref="VoteAverage"/>.</summary>
    public int VoteCount { get; init; }

    /// <summary>The TMDB genre identifiers tagged on the title; resolve to names via the catalog.</summary>
    public IReadOnlyList<int> GenreIds { get; init; } = [];
}
