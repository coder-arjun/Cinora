using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Tmdb;
using Cinora.Application.Features.Discovery;
using Cinora.Domain.Enums;
using Cinora.Infrastructure.Options;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cinora.Infrastructure.Tmdb;

/// <summary>
/// A cache-aside decorator over the raw <see cref="TmdbClient"/> (ADR 0007). It wraps the inner
/// <see cref="ITmdbClient"/> and, for every method, tries the free in-memory <see cref="IDistributedCache"/>
/// first (System.Text.Json round-trip of the Application read model), calls the inner client on a miss, then
/// stores the successful result under a stable <c>cinora:tmdb:*</c> key with a per-resource TTL. Handlers
/// depend only on <see cref="ITmdbClient"/> and are oblivious to caching (SRP: the raw client does HTTP +
/// mapping; this decorator adds cache-aside + graceful degradation). Three invariants from ADR 0007:
/// <list type="bullet">
///   <item>never cache a null/empty miss (a 404 details response and an empty search page are re-fetched so
///   a later populate is picked up);</item>
///   <item>degrade, never error — any cache get/set failure is logged and the request falls through to the
///   inner client, so a cache outage never surfaces as an error;</item>
///   <item>±10% per-write TTL jitter so co-created hot keys do not expire in unison (stampede guard).</item>
/// </list>
/// Internal: the composition root registers it wrapping the concrete <see cref="TmdbClient"/>.
/// </summary>
internal sealed class CachedTmdbClient : ITmdbClient
{
    // Per-resource multipliers of CacheOptions.DefaultTtlSeconds (default 300s = 5 min). With the default
    // base these yield the ADR 0007 TTLs — search 15m, trending 1h, popular 3h, top-rated 12h,
    // details/genres 24h — while scaling from one operator-tunable base if DefaultTtlSeconds is changed.
    private const double SearchTtlFactor = 3;      // 15 min: search churns; keep it short to bound staleness.
    private const double TrendingTtlFactor = 12;   // 1 h: the freshest home rail.
    private const double PopularTtlFactor = 36;    // 3 h.
    private const double TopRatedTtlFactor = 144;  // 12 h: changes slowly.
    private const double DetailsTtlFactor = 288;   // 24 h: title metadata changes rarely.
    private const double GenresTtlFactor = 288;    // 24 h: a small, bounded set.
    private const double RecommendationsTtlFactor = 144;  // 12 h: a title's "more like this" set changes slowly (§5.3).
    private const double DiscoverTtlFactor = 72;   // 6 h: genre-popularity discovery drifts a little faster (§5.3).
    private const double RegionalTtlFactor = 72;   // 6 h: original-language discover rails drift like genre-discover.

    // ±10% jitter, drawn per write, so hot keys created together do not expire together (ADR 0007 stampede
    // guard). Varying by write (not a single fixed value) is the point.
    private const double JitterFraction = 0.10;

    // The one key namespace that embeds user free text (the normalized query) — used both to build the key and
    // to detect+redact it in logs (backlog 2.5). Every other key namespace embeds only ids/segments.
    private const string SearchKeyPrefix = "cinora:tmdb:search:";

    private readonly ITmdbClient _inner;
    private readonly IDistributedCache _cache;
    private readonly ILogger<CachedTmdbClient> _logger;
    private readonly int _defaultTtlSeconds;

    /// <summary>Creates the decorator over an inner client, the cache, the cache options, and a logger.</summary>
    /// <param name="inner">The wrapped client (the raw <see cref="TmdbClient"/>), called on a cache miss.</param>
    /// <param name="cache">The distributed cache (the free in-memory provider by default).</param>
    /// <param name="options">The cache options supplying <see cref="CacheOptions.DefaultTtlSeconds"/>.</param>
    /// <param name="logger">The logger used to record cache degradation at Warning.</param>
    public CachedTmdbClient(
        ITmdbClient inner,
        IDistributedCache cache,
        IOptions<CacheOptions> options,
        ILogger<CachedTmdbClient> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _cache = cache;
        _logger = logger;
        _defaultTtlSeconds = options.Value.DefaultTtlSeconds;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbTitleSummary>> GetTrendingAsync(MediaType media, CancellationToken cancellationToken) =>
        GetOrSetAsync(
            $"cinora:tmdb:rail:trending:{Segment(media)}:1",
            TrendingTtlFactor,
            ct => _inner.GetTrendingAsync(media, ct),
            HasItems,
            cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbTitleSummary>> GetPopularAsync(MediaType media, CancellationToken cancellationToken) =>
        GetOrSetAsync(
            $"cinora:tmdb:rail:popular:{Segment(media)}:1",
            PopularTtlFactor,
            ct => _inner.GetPopularAsync(media, ct),
            HasItems,
            cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbTitleSummary>> GetTopRatedAsync(MediaType media, CancellationToken cancellationToken) =>
        GetOrSetAsync(
            $"cinora:tmdb:rail:toprated:{Segment(media)}:1",
            TopRatedTtlFactor,
            ct => _inner.GetTopRatedAsync(media, ct),
            HasItems,
            cancellationToken);

    /// <inheritdoc />
    public Task<TmdbPage<TmdbTitleSummary>> SearchAsync(MediaType media, string query, int page, CancellationToken cancellationToken) =>
        GetOrSetAsync(
            $"{SearchKeyPrefix}{Segment(media)}:{Normalize(query)}:{page}",
            SearchTtlFactor,
            ct => _inner.SearchAsync(media, query, page, ct),
            // Never cache an empty result set: a later populate (TMDB indexing the title) must be picked up,
            // not masked by a 15-minute cached miss (ADR 0007).
            static result => result is { Items.Count: > 0 },
            cancellationToken);

    /// <inheritdoc />
    public Task<TmdbTitleDetails?> GetDetailsAsync(MediaType media, int tmdbId, CancellationToken cancellationToken) =>
        GetOrSetAsync<TmdbTitleDetails?>(
            $"cinora:tmdb:details:{Segment(media)}:{tmdbId}",
            DetailsTtlFactor,
            ct => _inner.GetDetailsAsync(media, tmdbId, ct),
            // A 404 maps to null and must not be cached, so a later add to TMDB is seen (ADR 0007).
            static details => details is not null,
            cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbGenre>> GetGenresAsync(MediaType media, CancellationToken cancellationToken) =>
        GetOrSetAsync(
            $"cinora:tmdb:genres:{Segment(media)}",
            GenresTtlFactor,
            ct => _inner.GetGenresAsync(media, ct),
            HasItems,
            cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbTitleSummary>> GetRecommendationsAsync(MediaType media, int tmdbId, CancellationToken cancellationToken) =>
        GetOrSetAsync(
            $"cinora:tmdb:recs:{Segment(media)}:{tmdbId}",
            RecommendationsTtlFactor,
            ct => _inner.GetRecommendationsAsync(media, tmdbId, ct),
            HasItems,
            cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbTitleSummary>> DiscoverByGenreAsync(MediaType media, IReadOnlyList<int> genreTmdbIds, CancellationToken cancellationToken) =>
        GetOrSetAsync(
            // Order the ids deterministically so [18,28] and [28,18] share ONE key (the discover result is the
            // same set either way); the inner client returns [] for an empty genre set, which HasItems never caches.
            $"cinora:tmdb:discover:{Segment(media)}:{string.Join(",", genreTmdbIds.OrderBy(id => id))}",
            DiscoverTtlFactor,
            ct => _inner.DiscoverByGenreAsync(media, genreTmdbIds, ct),
            HasItems,
            cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<TmdbTitleSummary>> GetRegionalRailAsync(
        MediaType media, RailKind kind, string originalLanguage, CancellationToken cancellationToken) =>
        GetOrSetAsync(
            // Key embeds media segment + lower-cased language + rail kind (all ids/enums, no free text), so two
            // languages or kinds never collide on one entry and RedactKeyForLog can log it verbatim.
            $"cinora:tmdb:regional:{Segment(media)}:{originalLanguage.ToLowerInvariant()}:{RailKindKey(kind)}",
            RegionalTtlFactor,
            ct => _inner.GetRegionalRailAsync(media, kind, originalLanguage, ct),
            HasItems,
            cancellationToken);

    // Cache-aside core: try the cache, call the source on a miss, then store only cacheable (non-null,
    // non-empty) results. Cache failures never propagate — the request always resolves from the source.
    private async Task<T> GetOrSetAsync<T>(
        string key,
        double ttlFactor,
        Func<CancellationToken, Task<T>> fetch,
        Func<T, bool> isCacheable,
        CancellationToken cancellationToken)
    {
        var (found, cached) = await TryReadAsync<T>(key, cancellationToken);
        if (found)
        {
            return cached!;
        }

        var value = await fetch(cancellationToken);

        if (isCacheable(value))
        {
            await TryWriteAsync(key, value, ttlFactor, cancellationToken);
        }

        return value;
    }

    private async Task<(bool Found, T? Value)> TryReadAsync<T>(string key, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await _cache.GetAsync(key, cancellationToken);
            if (bytes is not { Length: > 0 })
            {
                return (false, default);
            }

            var value = JsonSerializer.Deserialize<T>(bytes);
            return value is null ? (false, default) : (true, value);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Degrade, never error: a cache read failure (or a corrupt entry) falls through to the source.
            CacheLog.ReadFailed(_logger, exception, RedactKeyForLog(key));
            return (false, default);
        }
    }

    private async Task TryWriteAsync<T>(string key, T value, double ttlFactor, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ComputeTtl(ttlFactor) };
            await _cache.SetAsync(key, bytes, options, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Degrade, never error: a cache write failure still serves the freshly fetched value.
            CacheLog.WriteFailed(_logger, exception, RedactKeyForLog(key));
        }
    }

    // Milestone 6.4 (§6.5, backlog 2.5 sec Low): the search cache key embeds the user's normalized free-text
    // query (cinora:tmdb:search:{seg}:{query}:{page}), so logging it verbatim on a rare cache-infra Warning would
    // put a user's search terms in the logs. Redact the query for search keys — keep the readable prefix + a
    // stable, non-reversible hash so distinct failing keys stay distinguishable without disclosing the terms.
    // Every OTHER key embeds only ids/segments (no free text), so it is logged unchanged.
    private static string RedactKeyForLog(string key)
    {
        if (!key.StartsWith(SearchKeyPrefix, StringComparison.Ordinal))
        {
            return key;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
        return $"{SearchKeyPrefix}<redacted:{hash}>";
    }

    private TimeSpan ComputeTtl(double ttlFactor)
    {
        // Random.Shared is thread-safe; NextDouble() is [0,1) → jitter multiplier in [0.9, 1.1).
        var jitter = 1.0 + (((Random.Shared.NextDouble() * 2.0) - 1.0) * JitterFraction);
        return TimeSpan.FromSeconds(_defaultTtlSeconds * ttlFactor * jitter);
    }

    // TMDB uses "movie" and "tv" path segments; reused here for stable, readable cache keys.
    private static string Segment(MediaType media) => media == MediaType.Series ? "tv" : "movie";

    // Stable lower-case rail-kind token for the regional cache key (mirrors the rail: literals above).
    private static string RailKindKey(RailKind kind) => kind switch
    {
        RailKind.Trending => "trending",
        RailKind.Popular => "popular",
        RailKind.TopRated => "toprated",
        _ => "unknown",
    };

    // Case-fold + trim so "Batman", " batman " and "BATMAN" share one key, bounding search-key cardinality
    // (ADR 0007). The fold direction is immaterial for a key; ToUpperInvariant is the analyzer-preferred
    // form (CA1308). The original, unfolded query is still what is sent to TMDB on a miss.
    private static string Normalize(string query) => query.Trim().ToUpperInvariant();

    private static bool HasItems<T>(IReadOnlyList<T> items) => items.Count > 0;
}
