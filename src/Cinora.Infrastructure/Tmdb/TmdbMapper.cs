using System.Globalization;
using Cinora.Application.Common.Tmdb;
using Cinora.Domain.Enums;
using Cinora.Infrastructure.Tmdb.Dtos;

namespace Cinora.Infrastructure.Tmdb;

/// <summary>
/// Maps internal TMDB wire DTOs to Application read models. This is the single boundary where TMDB's
/// snake_case shapes are translated; the DTOs never travel past it (ADR 0006). Internal and stateless.
/// </summary>
internal static class TmdbMapper
{
    // Bounds the cast carried on details (and later cached): the view renders only the top billing, so a
    // full 100+ person credits list would bloat the payload for no display benefit.
    private const int MaxCastMembers = 20;

    /// <summary>Maps a TMDB list/trending/search page to its title summaries (empty when null/absent).</summary>
    public static IReadOnlyList<TmdbTitleSummary> ToSummaries(TmdbPagedDto<TmdbListItemDto>? page, MediaType media)
    {
        if (page?.Results is null || page.Results.Count == 0)
        {
            return [];
        }

        var summaries = new List<TmdbTitleSummary>(page.Results.Count);
        foreach (var item in page.Results)
        {
            summaries.Add(ToSummary(item, media));
        }

        return summaries;
    }

    /// <summary>Maps a TMDB search page (envelope + results) to a paged read model.</summary>
    public static TmdbPage<TmdbTitleSummary> ToPage(TmdbPagedDto<TmdbListItemDto>? page, MediaType media) =>
        new()
        {
            Page = page?.Page ?? 0,
            TotalPages = page?.TotalPages ?? 0,
            TotalResults = page?.TotalResults ?? 0,
            Items = ToSummaries(page, media),
        };

    /// <summary>Maps a TMDB details response to the details read model, or <c>null</c> when absent.</summary>
    public static TmdbTitleDetails? ToDetails(TmdbDetailsDto? dto, MediaType media)
    {
        if (dto is null)
        {
            return null;
        }

        return new TmdbTitleDetails
        {
            TmdbId = dto.Id,
            MediaType = media,
            Title = dto.Title ?? dto.Name ?? string.Empty,
            Overview = Normalize(dto.Overview),
            Tagline = Normalize(dto.Tagline),
            PosterPath = dto.PosterPath,
            BackdropPath = dto.BackdropPath,
            ReleaseDate = ParseDate(dto.ReleaseDate ?? dto.FirstAirDate),
            // The DTO numbers are nullable so a TMDB null/omission cannot fail deserialization; default them
            // here so the read model keeps its non-nullable public shape (an unvoted title reads as 0/0).
            VoteAverage = dto.VoteAverage ?? 0,
            VoteCount = dto.VoteCount ?? 0,
            Runtime = dto.Runtime ?? FirstRunTime(dto.EpisodeRunTime),
            Genres = MapGenres(dto.Genres),
            Cast = MapCast(dto.Credits?.Cast),
        };
    }

    /// <summary>Maps a TMDB genre-list response to genre read models (empty when null/absent).</summary>
    public static IReadOnlyList<TmdbGenre> ToGenres(TmdbGenreListDto? dto) => MapGenres(dto?.Genres);

    private static TmdbTitleSummary ToSummary(TmdbListItemDto item, MediaType media) =>
        new()
        {
            TmdbId = item.Id,
            MediaType = media,
            Title = item.Title ?? item.Name ?? string.Empty,
            Overview = Normalize(item.Overview),
            PosterPath = item.PosterPath,
            BackdropPath = item.BackdropPath,
            ReleaseDate = ParseDate(item.ReleaseDate ?? item.FirstAirDate),
            // Default the nullable DTO numbers so a TMDB null cannot fail deserialization (see ToDetails).
            VoteAverage = item.VoteAverage ?? 0,
            VoteCount = item.VoteCount ?? 0,
            GenreIds = item.GenreIds ?? [],
        };

    // Returns the concrete List<T> (CA1859): callers within the assembly avoid interface dispatch.
    private static List<TmdbGenre> MapGenres(IReadOnlyList<TmdbGenreDto>? genres)
    {
        if (genres is null || genres.Count == 0)
        {
            return [];
        }

        var mapped = new List<TmdbGenre>(genres.Count);
        foreach (var genre in genres)
        {
            mapped.Add(new TmdbGenre { TmdbGenreId = genre.Id, Name = genre.Name ?? string.Empty });
        }

        return mapped;
    }

    // Returns the concrete List<T> (CA1859): callers within the assembly avoid interface dispatch.
    private static List<TmdbCastMember> MapCast(IReadOnlyList<TmdbCastMemberDto>? cast)
    {
        if (cast is null || cast.Count == 0)
        {
            return [];
        }

        return cast
            // Order is nullable on the DTO; rank an unknown billing last. STORE the same last-ranking
            // sentinel (int.MaxValue) rather than 0 — 0 means top-billed — so the stored Order stays
            // consistent with THIS sort key: a later re-sort of the read model by Order keeps unknown-order
            // members last instead of promoting them to the front.
            .OrderBy(member => member.Order ?? int.MaxValue)
            .Take(MaxCastMembers)
            .Select(member => new TmdbCastMember
            {
                TmdbId = member.Id,
                Name = member.Name ?? string.Empty,
                Character = Normalize(member.Character),
                ProfilePath = member.ProfilePath,
                Order = member.Order ?? int.MaxValue,
            })
            .ToList();
    }

    private static int? FirstRunTime(IReadOnlyList<int>? runtimes) =>
        runtimes is { Count: > 0 } ? runtimes[0] : null;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
