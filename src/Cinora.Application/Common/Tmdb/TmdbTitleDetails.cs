using Cinora.Domain.Enums;

namespace Cinora.Application.Common.Tmdb;

/// <summary>
/// An Application-owned read model for a single TMDB title's full detail (the detail page), including its
/// genres and a ranked cast summary fetched in one call via TMDB's <c>append_to_response=credits</c>. Part
/// of the <see cref="Interfaces.ITmdbClient"/> contract: TMDB's snake_case wire DTOs are mapped to this
/// shape inside Infrastructure and never leak outward (ADR 0006). Image fields carry the raw TMDB path; the
/// view builds a sized URL via <see cref="Interfaces.ITmdbImageUrlBuilder"/>.
/// </summary>
public sealed record TmdbTitleDetails
{
    /// <summary>The TMDB identifier of the title.</summary>
    public required int TmdbId { get; init; }

    /// <summary>Whether the title is a movie or a series.</summary>
    public required MediaType MediaType { get; init; }

    /// <summary>The display title (TMDB <c>title</c> for movies, <c>name</c> for series).</summary>
    public required string Title { get; init; }

    /// <summary>The synopsis, or <c>null</c> when TMDB provides none.</summary>
    public string? Overview { get; init; }

    /// <summary>The tagline, or <c>null</c> when TMDB provides none.</summary>
    public string? Tagline { get; init; }

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

    /// <summary>
    /// The runtime in minutes (a movie's <c>runtime</c>, or a series' first <c>episode_run_time</c>),
    /// or <c>null</c> when TMDB provides none.
    /// </summary>
    public int? Runtime { get; init; }

    /// <summary>The genres tagged on the title.</summary>
    public IReadOnlyList<TmdbGenre> Genres { get; init; } = [];

    /// <summary>The cast, ranked by billing order (top-billed first).</summary>
    public IReadOnlyList<TmdbCastMember> Cast { get; init; } = [];
}
