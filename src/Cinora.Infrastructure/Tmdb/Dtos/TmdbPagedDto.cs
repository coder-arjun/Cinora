using System.Text.Json.Serialization;

namespace Cinora.Infrastructure.Tmdb.Dtos;

// TMDB wire DTOs are internal to Infrastructure and never leave it (ADR 0006): they deserialize TMDB's
// snake_case JSON and are mapped to Application read models by TmdbMapper. They are init-only records so
// System.Text.Json can populate them, and carry both movie (title/release_date) and series
// (name/first_air_date) field names since the two share one list/search envelope shape.

/// <summary>The TMDB paged envelope wrapping a list of <typeparamref name="T"/> results.</summary>
/// <typeparam name="T">The wire DTO type of each result.</typeparam>
internal sealed record TmdbPagedDto<T>
{
    // Nullable: the paging numbers are always sent on a normal TMDB envelope, but a null-tolerant DTO keeps
    // a malformed/edge response (an explicit null) from making System.Text.Json throw; TmdbMapper defaults
    // them to 0. (Marking them non-nullable buys nothing here — the mapper already coalesces via page?.)
    [JsonPropertyName("page")]
    public int? Page { get; init; }

    [JsonPropertyName("total_pages")]
    public int? TotalPages { get; init; }

    [JsonPropertyName("total_results")]
    public int? TotalResults { get; init; }

    [JsonPropertyName("results")]
    public IReadOnlyList<T>? Results { get; init; }
}

/// <summary>A single title as it appears in a TMDB list, trending, or search response.</summary>
internal sealed record TmdbListItemDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

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

    // Nullable: TMDB may send an explicit JSON null (or omit) these vote numbers on unvoted titles; a
    // non-nullable value type would make System.Text.Json throw on null. TmdbMapper defaults them to 0.
    [JsonPropertyName("vote_average")]
    public double? VoteAverage { get; init; }

    [JsonPropertyName("vote_count")]
    public int? VoteCount { get; init; }

    [JsonPropertyName("genre_ids")]
    public IReadOnlyList<int>? GenreIds { get; init; }
}
