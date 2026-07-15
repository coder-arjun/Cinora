using Microsoft.Extensions.Logging;

namespace Cinora.Infrastructure.Tmdb;

/// <summary>
/// Source-generated, high-performance log messages for <see cref="CachedTmdbClient"/>. Using
/// <see cref="LoggerMessageAttribute"/> (CA1848) avoids allocations and evaluates arguments only when the
/// level is enabled. A cache read or write failure is logged at Warning and then swallowed: the decorator
/// degrades to the inner TMDB client rather than surfacing a cache outage as an error (ADR 0007 —
/// "degrade, never error"). Defined in a dedicated non-generic partial type because the logging source
/// generator does not emit for generic containing types.
/// </summary>
internal static partial class CacheLog
{
    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Warning,
        Message = "TMDB cache read failed for {CacheKey}; falling through to the source")]
    public static partial void ReadFailed(ILogger logger, Exception exception, string cacheKey);

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Warning,
        Message = "TMDB cache write failed for {CacheKey}; the value was served but not cached")]
    public static partial void WriteFailed(ILogger logger, Exception exception, string cacheKey);
}
