using System.Text.Json.Serialization;

namespace Cinora.Infrastructure.Tmdb.Dtos;

/// <summary>
/// A TMDB title-details response (movie or series) fetched with <c>append_to_response=credits</c>. One DTO
/// covers both media types: movies carry <c>title</c>/<c>release_date</c>/<c>runtime</c>, series carry
/// <c>name</c>/<c>first_air_date</c>/<c>episode_run_time</c>.
/// </summary>
internal sealed record TmdbDetailsDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("overview")]
    public string? Overview { get; init; }

    [JsonPropertyName("tagline")]
    public string? Tagline { get; init; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; init; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; init; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; init; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; init; }

    // Nullable: TMDB may send an explicit JSON null (or omit the field) for community vote numbers on
    // titles with no votes yet. A non-nullable value-type member would make System.Text.Json throw on
    // null and fail the whole fetch; TmdbMapper defaults these back to 0 for the read model.
    [JsonPropertyName("vote_average")]
    public double? VoteAverage { get; init; }

    [JsonPropertyName("vote_count")]
    public int? VoteCount { get; init; }

    [JsonPropertyName("runtime")]
    public int? Runtime { get; init; }

    [JsonPropertyName("episode_run_time")]
    public IReadOnlyList<int>? EpisodeRunTime { get; init; }

    [JsonPropertyName("genres")]
    public IReadOnlyList<TmdbGenreDto>? Genres { get; init; }

    [JsonPropertyName("credits")]
    public TmdbCreditsDto? Credits { get; init; }
}

/// <summary>The appended <c>credits</c> object of a TMDB title-details response.</summary>
internal sealed record TmdbCreditsDto
{
    [JsonPropertyName("cast")]
    public IReadOnlyList<TmdbCastMemberDto>? Cast { get; init; }
}

/// <summary>A single cast entry within a TMDB <c>credits.cast</c> array.</summary>
internal sealed record TmdbCastMemberDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("character")]
    public string? Character { get; init; }

    [JsonPropertyName("profile_path")]
    public string? ProfilePath { get; init; }

    // Nullable: TMDB occasionally omits or nulls a cast member's billing order. Left non-nullable it would
    // make System.Text.Json throw on null; TmdbMapper defaults it (0 when assigned, last when ordering).
    [JsonPropertyName("order")]
    public int? Order { get; init; }
}
