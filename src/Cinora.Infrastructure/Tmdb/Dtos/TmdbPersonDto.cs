using System.Text.Json.Serialization;

namespace Cinora.Infrastructure.Tmdb.Dtos;

/// <summary>
/// A TMDB person response fetched with <c>append_to_response=combined_credits</c> (feature #9): the person's
/// name + profile image, plus their acting credits across movies and series. Internal to Infrastructure and
/// mapped to <see cref="Cinora.Application.Common.Tmdb.TmdbPersonCredits"/> by <see cref="TmdbMapper"/> (ADR 0006).
/// </summary>
internal sealed record TmdbPersonDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("profile_path")]
    public string? ProfilePath { get; init; }

    [JsonPropertyName("combined_credits")]
    public TmdbCombinedCreditsDto? CombinedCredits { get; init; }
}

/// <summary>The appended <c>combined_credits</c> object of a TMDB person response.</summary>
internal sealed record TmdbCombinedCreditsDto
{
    [JsonPropertyName("cast")]
    public IReadOnlyList<TmdbPersonCreditDto>? Cast { get; init; }
}

/// <summary>
/// A single acting credit within a person's <c>combined_credits.cast</c> array. Carries a per-item
/// <c>media_type</c> ("movie"/"tv") since a person's credits span both, plus both the movie
/// (<c>title</c>/<c>release_date</c>) and series (<c>name</c>/<c>first_air_date</c>) field names.
/// </summary>
internal sealed record TmdbPersonCreditDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("overview")]
    public string? Overview { get; init; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; init; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; init; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; init; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; init; }

    // Nullable for the same reason as the other list DTOs: TMDB may null/omit vote numbers on unvoted titles.
    [JsonPropertyName("vote_average")]
    public double? VoteAverage { get; init; }

    [JsonPropertyName("vote_count")]
    public int? VoteCount { get; init; }

    [JsonPropertyName("genre_ids")]
    public IReadOnlyList<int>? GenreIds { get; init; }
}
