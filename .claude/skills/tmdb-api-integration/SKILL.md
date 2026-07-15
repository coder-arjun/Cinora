---
name: tmdb-api-integration
description: Use when fetching movie or series metadata from TMDB in Cinora — typed HttpClient setup, 429/transient failures, mapping TMDB responses to Movie/Genre entities, poster image URLs, or deciding what to cache.
---

# TMDB API Integration

## Overview
All TMDB access goes through one typed `HttpClient` (`ITmdbClient`) with resilience built in; TMDB DTOs never leave the Infrastructure layer — they are mapped to `Movie`/`Genre` domain shapes and cached in Redis.

## Quick Reference
| Task | Approach |
|---|---|
| Client | `AddHttpClient<ITmdbClient, TmdbClient>` + `AddStandardResilienceHandler()` |
| Auth | v4 Bearer token from `TmdbOptions` (user-secrets) |
| Rate limits | ~50 req/s soft limit; resilience handler honors 429 `Retry-After` |
| DTO → domain | Map in `TmdbClient`/mapper; store `TmdbId` on `Movie` for upserts |
| Genres | Fetch `/genre/movie/list` once, seed `Genre` table, map `genre_ids` |
| Poster URL | `https://image.tmdb.org/t/p/{size}{poster_path}` — `w185`, `w342`, `w500`, `original` |
| Caching | Redis, TTL 6–24 h, key `cinora:tmdb:movie:{tmdbId}` |

## Pattern
```csharp
builder.Services.AddOptions<TmdbOptions>()
    .BindConfiguration(TmdbOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart(); // WHY: fail at boot, not on the first movie page view

builder.Services.AddHttpClient<ITmdbClient, TmdbClient>((sp, http) =>
{
    var opts = sp.GetRequiredService<IOptions<TmdbOptions>>().Value;
    http.BaseAddress = new Uri(opts.BaseUrl); // https://api.themoviedb.org/3/
    // WHY: v4 bearer header instead of ?api_key= — keeps the key out of logs and URLs
    http.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", opts.AccessToken);
})
// WHY: Microsoft.Extensions.Http.Resilience default pipeline = rate-limiter,
// total timeout, retry with backoff (respects Retry-After on 429), circuit breaker,
// attempt timeout — exactly what a flaky third-party API needs
.AddStandardResilienceHandler();

public sealed class TmdbClient(HttpClient http) : ITmdbClient
{
    public async Task<TmdbMovieDto?> GetMovieAsync(int tmdbId, CancellationToken ct) =>
        await http.GetFromJsonAsync<TmdbMovieDto>($"movie/{tmdbId}", ct);
}
```

DTOs use `[JsonPropertyName("poster_path")]`-style attributes for TMDB's snake_case. The nightly sync job (`.claude/skills/hangfire-background-jobs/SKILL.md`) upserts by `TmdbId` so re-runs are idempotent.

## Images
TMDB returns only a path like `/abc123.jpg` (leading slash included). Build URLs as `{ImageBaseUrl}/{size}{poster_path}`, choosing size per context: `w185` for feed cards, `w342` for lists, `w500` for detail pages. Never download and re-host TMDB images — hotlinking their CDN is the supported model (allow it in CSP, see `.claude/skills/security-hardening/SKILL.md`).

## Common Mistakes
| Mistake | Fix |
|---|---|
| TMDB DTOs leaking into views/domain | Map to domain/read models at the Infrastructure boundary |
| No resilience handler | Transient TMDB blips become user-facing 500s |
| Caching 404/empty responses | Only cache successful, non-null payloads (see `.claude/skills/redis-caching/SKILL.md`) |
| Hitting TMDB per page view | Serve from `Movie` table + Redis; TMDB is a sync source |
| Hardcoding image sizes/base URL | Put `ImageBaseUrl` and sizes in `TmdbOptions` |
| API key in query string or source | Bearer header, key via user-secrets/Key Vault |
