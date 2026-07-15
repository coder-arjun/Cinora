using System.Text.Json;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Features.Recommendations;
using Cinora.Infrastructure.Options;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cinora.Infrastructure.Ai;

/// <summary>
/// The <see cref="IServedRecommendationCache"/> adapter over the free in-memory <see cref="IDistributedCache"/>
/// (ADR 0018 §2, Phase 5 design §7.1). It owns every mechanic the port hides from the handlers (SRP, exactly like
/// <c>CachedTmdbClient</c>): the per-user key <c>cinora:ai:recs:{userId}</c>, a System.Text.Json round-trip of the
/// Application <see cref="RecommendationSet"/>, a ≈24 h TTL (<see cref="RecommendationOptions.ServedCacheTtlHours"/>,
/// aligned to the nightly precompute) with ±10% jitter so co-warmed keys do not expire in unison, and — critically
/// — <b>graceful degradation</b>: every read/write is wrapped so a cache fault logs a Warning and returns a
/// miss / no-ops; a cache outage NEVER surfaces as an error. Stateless over the singleton
/// <see cref="IDistributedCache"/>, so registered as a singleton. Internal: the composition root registers it
/// behind the Application port.
/// </summary>
internal sealed class ServedRecommendationCache : IServedRecommendationCache
{
    // ±10% per-write jitter, drawn per write, so keys warmed together by one precompute batch do not expire in
    // unison (the ADR 0007 stampede guard, reused).
    private const double JitterFraction = 0.10;

    private readonly IDistributedCache _cache;
    private readonly ILogger<ServedRecommendationCache> _logger;
    private readonly int _ttlHours;

    /// <summary>Creates the adapter over the distributed cache, the recommendation options, and a logger.</summary>
    /// <param name="cache">The distributed cache (the free in-memory provider by default).</param>
    /// <param name="options">The recommendation options supplying <see cref="RecommendationOptions.ServedCacheTtlHours"/>.</param>
    /// <param name="logger">The logger used to record cache degradation at Warning.</param>
    public ServedRecommendationCache(
        IDistributedCache cache,
        IOptions<RecommendationOptions> options,
        ILogger<ServedRecommendationCache> logger)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _cache = cache;
        _logger = logger;
        _ttlHours = options.Value.ServedCacheTtlHours;
    }

    /// <inheritdoc />
    public async Task<RecommendationSet?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var key = Key(userId);
        try
        {
            var bytes = await _cache.GetAsync(key, cancellationToken);
            if (bytes is not { Length: > 0 })
            {
                return null;
            }

            return JsonSerializer.Deserialize<RecommendationSet>(bytes);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Degrade, never error: a cache read failure (or a corrupt entry) is a miss — the query falls through
            // to the history row / heuristic.
            ServedRecommendationCacheLog.ReadFailed(_logger, exception, key);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(Guid userId, RecommendationSet set, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(set);

        var key = Key(userId);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(set);
            var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ComputeTtl() };
            await _cache.SetAsync(key, bytes, options, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Degrade, never error: a warm failure is swallowed — the durable history row already holds the set.
            ServedRecommendationCacheLog.WriteFailed(_logger, exception, key);
        }
    }

    private static string Key(Guid userId) => $"cinora:ai:recs:{userId}";

    private TimeSpan ComputeTtl()
    {
        // Random.Shared is thread-safe; NextDouble() is [0,1) → jitter multiplier in [0.9, 1.1).
        var jitter = 1.0 + (((Random.Shared.NextDouble() * 2.0) - 1.0) * JitterFraction);
        return TimeSpan.FromHours(_ttlHours * jitter);
    }
}
