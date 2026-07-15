using Microsoft.Extensions.Logging;

namespace Cinora.Infrastructure.Jobs;

/// <summary>
/// Source-generated, high-performance log messages for the <see cref="RecommendationPrecomputeJob"/> (CA1848 —
/// arguments are evaluated only when the level is enabled). Metrics-only: user ids and counts, never prompt
/// content or a model response body (the Phase 5 §12 hygiene rule). Defined in a dedicated non-generic partial
/// type because the logging source generator does not emit for generic containing types.
/// </summary>
internal static partial class PrecomputeLog
{
    [LoggerMessage(
        EventId = 2320,
        Level = LogLevel.Information,
        Message = "Recommendation precompute batch started")]
    public static partial void BatchStarted(ILogger logger);

    [LoggerMessage(
        EventId = 2321,
        Level = LogLevel.Information,
        Message = "Recommendation precompute selected {StaleCount} stale user(s) for regeneration; skipped {SkippedCount} with unchanged taste")]
    public static partial void UsersSelected(ILogger logger, int staleCount, int skippedCount);

    [LoggerMessage(
        EventId = 2322,
        Level = LogLevel.Debug,
        Message = "Recommendation precompute dispatched generation for user {UserId}")]
    public static partial void UserDispatched(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = 2323,
        Level = LogLevel.Warning,
        Message = "Recommendation precompute failed for user {UserId}; continuing with the rest of the batch")]
    public static partial void UserFailed(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(
        EventId = 2324,
        Level = LogLevel.Information,
        Message = "Recommendation precompute batch complete: {StaleCount} selected, {SkippedCount} skipped, {GeneratedCount} generated, {FailedCount} failed")]
    public static partial void BatchCompleted(
        ILogger logger, int staleCount, int skippedCount, int generatedCount, int failedCount);
}
