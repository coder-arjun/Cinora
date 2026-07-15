using Microsoft.Extensions.Logging;

namespace Cinora.Infrastructure.Ai;

/// <summary>
/// Source-generated, high-performance log messages for <see cref="ServedRecommendationCache"/> (CA1848 —
/// arguments are evaluated only when the level is enabled). A cache read or write failure is logged at Warning
/// and then swallowed: the adapter degrades to a miss / no-op rather than surfacing a cache outage as an error
/// (ADR 0018 §2 — the same "degrade, never crash" discipline as <c>CachedTmdbClient</c>). Metrics-only: the key
/// (no recommendation content). Defined in a dedicated non-generic partial type because the logging source
/// generator does not emit for generic containing types.
/// </summary>
internal static partial class ServedRecommendationCacheLog
{
    [LoggerMessage(
        EventId = 5100,
        Level = LogLevel.Warning,
        Message = "Served-recommendation cache read failed for {CacheKey}; treating as a miss")]
    public static partial void ReadFailed(ILogger logger, Exception exception, string cacheKey);

    [LoggerMessage(
        EventId = 5101,
        Level = LogLevel.Warning,
        Message = "Served-recommendation cache write failed for {CacheKey}; the set was served but not cached")]
    public static partial void WriteFailed(ILogger logger, Exception exception, string cacheKey);
}
