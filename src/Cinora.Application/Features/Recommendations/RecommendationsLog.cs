using Microsoft.Extensions.Logging;

namespace Cinora.Application.Features.Recommendations;

/// <summary>
/// Source-generated, high-performance log messages for the recommendation generate (<see
/// cref="GenerateRecommendationsCommandHandler"/>) and serve (<see cref="GetMyRecommendationsQueryHandler"/>)
/// handlers (CA1848 — arguments are evaluated only when the level is enabled). Metrics-only — user id and counts,
/// never prompt content or the model's response body (the Phase 5 §12 hygiene rule). Defined in a dedicated
/// non-generic partial type because the logging source generator does not emit for generic containing types.
/// </summary>
internal static partial class RecommendationsLog
{
    [LoggerMessage(
        EventId = 2300,
        Level = LogLevel.Information,
        Message = "Recommendation generation skipped for user {UserId}: thin taste profile (no reviews or watchlist); the engine was not called")]
    public static partial void SkippedThinProfile(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = 2301,
        Level = LogLevel.Information,
        Message = "Recommendation generation skipped for user {UserId}: no candidate titles were generated; the engine was not called")]
    public static partial void SkippedNoCandidates(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = 2302,
        Level = LogLevel.Warning,
        Message = "Recommendation engine was unavailable for user {UserId}; degrading to an empty set")]
    public static partial void EngineUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(
        EventId = 2303,
        Level = LogLevel.Warning,
        Message = "Recommendation hallucination guard dropped {DroppedCount} off-list pick(s) for user {UserId}")]
    public static partial void DroppedOffListPicks(ILogger logger, int droppedCount, Guid userId);

    [LoggerMessage(
        EventId = 2304,
        Level = LogLevel.Warning,
        Message = "Recommendation generation for user {UserId} yielded zero grounded picks from {ModelPickCount} model pick(s); treating as a generation miss")]
    public static partial void NoGroundedPicks(ILogger logger, Guid userId, int modelPickCount);

    [LoggerMessage(
        EventId = 2305,
        Level = LogLevel.Information,
        Message = "Recommendation generation for user {UserId} persisted {PickCount} pick(s) ({PromptTokens} prompt + {CompletionTokens} completion tokens) and warmed the served cache")]
    public static partial void GenerationPersisted(
        ILogger logger, Guid userId, int pickCount, int promptTokens, int completionTokens);

    [LoggerMessage(
        EventId = 2306,
        Level = LogLevel.Warning,
        Message = "Served-recommendation cache read failed for user {UserId}; falling through to the history/heuristic serve path")]
    public static partial void ServeCacheReadFailed(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(
        EventId = 2307,
        Level = LogLevel.Warning,
        Message = "The latest AIRecommendationHistory row for user {UserId} could not be parsed; falling through to the heuristic")]
    public static partial void HistoryParseFailed(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = 2308,
        Level = LogLevel.Warning,
        Message = "Heuristic recommender TMDB fan-out failed for user {UserId}; continuing with the titles gathered so far")]
    public static partial void HeuristicTmdbFailed(ILogger logger, Exception exception, Guid userId);
}
