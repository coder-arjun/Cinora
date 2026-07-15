using Cinora.Domain.Common;
using Cinora.Domain.Enums;

namespace Cinora.Domain.Entities;

/// <summary>
/// A cached movie or series sourced from TMDB. <see cref="TmdbId"/> is the stable external key
/// (unique per media type from Phase 2 onward); <see cref="Id"/> is Cinora's own surrogate key so
/// other entities reference titles by an internal <see cref="Guid"/>.
/// </summary>
public sealed class Movie
{
    /// <summary>The maximum allowed length of a <see cref="Title"/>.</summary>
    public const int TitleMaxLength = 500;

    private Movie()
    {
    }

    /// <summary>Cinora's internal identifier for the title.</summary>
    public Guid Id { get; private set; }

    /// <summary>The TMDB identifier this title was sourced from.</summary>
    public int TmdbId { get; private set; }

    /// <summary>Whether this title is a movie or a series.</summary>
    public MediaType MediaType { get; private set; }

    /// <summary>The display title.</summary>
    public string Title { get; private set; } = null!;

    /// <summary>The synopsis, or <c>null</c> when TMDB provides none.</summary>
    public string? Overview { get; private set; }

    /// <summary>The release date, or <c>null</c> when unknown.</summary>
    public DateTime? ReleaseDate { get; private set; }

    /// <summary>The TMDB poster image path, or <c>null</c> when none exists.</summary>
    public string? PosterPath { get; private set; }

    /// <summary>The TMDB backdrop image path, or <c>null</c> when none exists.</summary>
    public string? BackdropPath { get; private set; }

    /// <summary>The UTC instant this title's metadata was cached from TMDB.</summary>
    public DateTime CachedAtUtc { get; private set; }

    /// <summary>Creates a cached title from TMDB metadata.</summary>
    /// <param name="tmdbId">The TMDB identifier; must be greater than zero.</param>
    /// <param name="mediaType">Whether the title is a movie or a series.</param>
    /// <param name="title">The display title; required, max <see cref="TitleMaxLength"/> characters.</param>
    /// <param name="overview">The synopsis, or <c>null</c>.</param>
    /// <param name="releaseDate">The release date, or <c>null</c>.</param>
    /// <param name="posterPath">The TMDB poster path, or <c>null</c>.</param>
    /// <param name="backdropPath">The TMDB backdrop path, or <c>null</c>.</param>
    /// <returns>A newly cached <see cref="Movie"/>.</returns>
    public static Movie FromTmdb(
        int tmdbId,
        MediaType mediaType,
        string title,
        string? overview,
        DateTime? releaseDate,
        string? posterPath,
        string? backdropPath) =>
        new()
        {
            Id = Guid.NewGuid(),
            TmdbId = Guard.Positive(tmdbId, nameof(tmdbId)),
            MediaType = mediaType,
            Title = Guard.Required(title, TitleMaxLength, nameof(title)),
            Overview = overview,
            ReleaseDate = releaseDate,
            PosterPath = posterPath,
            BackdropPath = backdropPath,
            CachedAtUtc = DateTime.UtcNow,
        };
}
