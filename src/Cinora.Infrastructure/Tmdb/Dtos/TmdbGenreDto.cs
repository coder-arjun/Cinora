using System.Text.Json.Serialization;

namespace Cinora.Infrastructure.Tmdb.Dtos;

/// <summary>A single TMDB genre from a genre list or a title's <c>genres</c> array.</summary>
internal sealed record TmdbGenreDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>The TMDB <c>/genre/{media}/list</c> response envelope.</summary>
internal sealed record TmdbGenreListDto
{
    [JsonPropertyName("genres")]
    public IReadOnlyList<TmdbGenreDto>? Genres { get; init; }
}
