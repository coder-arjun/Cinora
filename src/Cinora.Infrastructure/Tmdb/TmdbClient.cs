using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Tmdb;
using Cinora.Application.Features.Discovery;
using Cinora.Domain.Enums;
using Cinora.Infrastructure.Tmdb.Dtos;

namespace Cinora.Infrastructure.Tmdb;

/// <summary>
/// The raw TMDB HTTP adapter behind <see cref="ITmdbClient"/>: a typed <see cref="HttpClient"/> (base
/// address + v4 Bearer default header + standard resilience pipeline configured at registration) that
/// deserializes TMDB JSON into internal DTOs and maps them to Application read models via
/// <see cref="TmdbMapper"/> (ADR 0006). Internal — the composition root registers it and the Milestone 2.1
/// caching decorator wraps it; nothing outside Infrastructure references the concrete type.
/// </summary>
internal sealed class TmdbClient : ITmdbClient
{
    // Product decision for Phase 2: English metadata. Not environment-varying, so a constant rather than a
    // TmdbOptions setting (revisit if internationalization is added).
    private const string Language = "en-US";

    private readonly HttpClient _http;

    /// <summary>Creates the adapter over a configured typed <see cref="HttpClient"/>.</summary>
    /// <param name="http">The typed client whose base address and Bearer header are set at registration.</param>
    public TmdbClient(HttpClient http) => _http = http;

    /// <inheritdoc />
    public async Task<IReadOnlyList<TmdbTitleSummary>> GetTrendingAsync(MediaType media, CancellationToken cancellationToken)
    {
        var page = await _http.GetFromJsonAsync<TmdbPagedDto<TmdbListItemDto>>(
            $"trending/{Segment(media)}/week?language={Language}",
            cancellationToken);

        return TmdbMapper.ToSummaries(page, media);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TmdbTitleSummary>> GetPopularAsync(MediaType media, CancellationToken cancellationToken)
    {
        var page = await _http.GetFromJsonAsync<TmdbPagedDto<TmdbListItemDto>>(
            $"{Segment(media)}/popular?language={Language}&page=1",
            cancellationToken);

        return TmdbMapper.ToSummaries(page, media);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TmdbTitleSummary>> GetTopRatedAsync(MediaType media, CancellationToken cancellationToken)
    {
        var page = await _http.GetFromJsonAsync<TmdbPagedDto<TmdbListItemDto>>(
            $"{Segment(media)}/top_rated?language={Language}&page=1",
            cancellationToken);

        return TmdbMapper.ToSummaries(page, media);
    }

    /// <inheritdoc />
    public async Task<TmdbPage<TmdbTitleSummary>> SearchAsync(MediaType media, string query, int page, CancellationToken cancellationToken)
    {
        var encodedQuery = Uri.EscapeDataString(query);
        var envelope = await _http.GetFromJsonAsync<TmdbPagedDto<TmdbListItemDto>>(
            $"search/{Segment(media)}?query={encodedQuery}&page={page}&include_adult=false&language={Language}",
            cancellationToken);

        return TmdbMapper.ToPage(envelope, media);
    }

    /// <inheritdoc />
    public async Task<TmdbPage<TmdbTitleSummary>> SearchMultiAsync(string query, int page, CancellationToken cancellationToken)
    {
        var encodedQuery = Uri.EscapeDataString(query);
        var envelope = await _http.GetFromJsonAsync<TmdbPagedDto<TmdbListItemDto>>(
            $"search/multi?query={encodedQuery}&page={page}&include_adult=false&language={Language}",
            cancellationToken);

        return TmdbMapper.ToMultiPage(envelope);
    }

    /// <inheritdoc />
    public async Task<TmdbTitleDetails?> GetDetailsAsync(MediaType media, int tmdbId, CancellationToken cancellationToken)
    {
        // Details uses GetAsync (not GetFromJsonAsync) so a 404 for an unknown title maps to null rather
        // than throwing — the caching decorator must not cache that null (Milestone 2.1).
        using var response = await _http.GetAsync(
            $"{Segment(media)}/{tmdbId}?append_to_response=credits&language={Language}",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var dto = await response.Content.ReadFromJsonAsync<TmdbDetailsDto>(cancellationToken);
        return TmdbMapper.ToDetails(dto, media);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TmdbGenre>> GetGenresAsync(MediaType media, CancellationToken cancellationToken)
    {
        var dto = await _http.GetFromJsonAsync<TmdbGenreListDto>(
            $"genre/{Segment(media)}/list?language={Language}",
            cancellationToken);

        return TmdbMapper.ToGenres(dto);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TmdbTitleSummary>> GetRecommendationsAsync(MediaType media, int tmdbId, CancellationToken cancellationToken)
    {
        var page = await _http.GetFromJsonAsync<TmdbPagedDto<TmdbListItemDto>>(
            $"{Segment(media)}/{tmdbId}/recommendations?language={Language}&page=1",
            cancellationToken);

        return TmdbMapper.ToSummaries(page, media);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TmdbTitleSummary>> DiscoverByGenreAsync(MediaType media, IReadOnlyList<int> genreTmdbIds, CancellationToken cancellationToken)
    {
        // No genres to discover within: return empty WITHOUT a TMDB call (an unfiltered discover would be a
        // generic popularity list, not a taste-grounded fan-out — ADR 0017).
        if (genreTmdbIds.Count == 0)
        {
            return [];
        }

        // TMDB's with_genres treats a comma-separated list as AND (match ALL) and a pipe-separated list as OR
        // (match ANY). The port contract is "tagged with ANY of the supplied genres" (ADR 0017 §5.3 "in your
        // favorite genres"), so the ids are joined with '|' — a comma would demand a title carry all ~5 favorite
        // genres at once, gutting the genre-grounded fan-out.
        var page = await _http.GetFromJsonAsync<TmdbPagedDto<TmdbListItemDto>>(
            $"discover/{Segment(media)}?with_genres={string.Join("|", genreTmdbIds)}&sort_by=popularity.desc&include_adult=false&language={Language}&page=1",
            cancellationToken);

        return TmdbMapper.ToSummaries(page, media);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TmdbTitleSummary>> GetRegionalRailAsync(
        MediaType media, RailKind kind, string originalLanguage, CancellationToken cancellationToken)
    {
        // TMDB `discover` with with_original_language returns titles ORIGINALLY in the language (regional cinema).
        // The rail kind maps to a sort (Top Rated → quality with a vote floor; else popularity) plus, for
        // Trending, a recent-release window so it differs from all-time Popular. The requested language is a
        // validated ISO-639-1 code (the Web layer's LanguageOptions gate), so it is safe to interpolate.
        var sort = kind == RailKind.TopRated ? "vote_average.desc" : "popularity.desc";
        var query =
            $"discover/{Segment(media)}?with_original_language={originalLanguage}&sort_by={sort}"
            + $"&include_adult=false&language={Language}&page=1";

        if (kind == RailKind.TopRated)
        {
            // A vote-count floor keeps "top rated" meaningful — a lone 10/10 vote must not top the rail.
            query += "&vote_count.gte=200";
        }
        else if (kind == RailKind.Trending)
        {
            // Distinguish Trending from Popular by counting only recent releases ("what's hot now" in the language).
            var since = DateTime.UtcNow.AddYears(-2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var dateParam = media == MediaType.Series ? "first_air_date.gte" : "primary_release_date.gte";
            query += $"&{dateParam}={since}";
        }

        var page = await _http.GetFromJsonAsync<TmdbPagedDto<TmdbListItemDto>>(query, cancellationToken);
        return TmdbMapper.ToSummaries(page, media);
    }

    /// <inheritdoc />
    public async Task<TmdbPersonCredits?> GetPersonCreditsAsync(int personId, CancellationToken cancellationToken)
    {
        // Like GetDetailsAsync, uses GetAsync so an unknown person (404) maps to null rather than throwing —
        // the caching decorator must not cache that null.
        using var response = await _http.GetAsync(
            $"person/{personId}?append_to_response=combined_credits&language={Language}",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var dto = await response.Content.ReadFromJsonAsync<TmdbPersonDto>(cancellationToken);
        return TmdbMapper.ToPersonCredits(dto);
    }

    // TMDB uses "movie" and "tv" path segments; the Domain enum distinguishes Movie from Series.
    private static string Segment(MediaType media) => media == MediaType.Series ? "tv" : "movie";
}
