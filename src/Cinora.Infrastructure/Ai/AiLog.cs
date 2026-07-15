using Microsoft.Extensions.Logging;

namespace Cinora.Infrastructure.Ai;

/// <summary>
/// Source-generated, high-performance log messages for <see cref="OllamaRecommendationEngine"/> (CA1848 —
/// arguments are evaluated only when the level is enabled). Every message is METRICS-ONLY — model name,
/// counts, token usage, timeout — and NEVER the prompt or the model's response body, so no user data (even the
/// PII-free titles and genres in the prompt) is written to the log (the Phase 5 §12 prompt-hygiene rule and
/// the ai-agent standard "log metrics, not content"). Defined in a dedicated non-generic partial type because
/// the logging source generator does not emit for generic containing types.
/// </summary>
internal static partial class AiLog
{
    [LoggerMessage(
        EventId = 5000,
        Level = LogLevel.Information,
        Message = "AI ranking succeeded for model {Model}: {PickCount} pick(s) from {CandidateCount} candidate(s), {PromptTokens} prompt + {CompletionTokens} completion tokens")]
    public static partial void RankSucceeded(
        ILogger logger, string model, int pickCount, int candidateCount, int promptTokens, int completionTokens);

    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Warning,
        Message = "AI model {Model} returned an empty response; degrading to no picks")]
    public static partial void EmptyResponse(ILogger logger, string model);

    [LoggerMessage(
        EventId = 5002,
        Level = LogLevel.Warning,
        Message = "AI model {Model} returned an unusable ({ResponseLength}-char) response that was not valid ranking JSON; degrading to no picks")]
    public static partial void UnusableResponse(ILogger logger, string model, int responseLength);

    [LoggerMessage(
        EventId = 5003,
        Level = LogLevel.Warning,
        Message = "AI ranking timed out after {TimeoutSeconds}s for model {Model}")]
    public static partial void RankTimedOut(ILogger logger, Exception exception, string model, int timeoutSeconds);

    [LoggerMessage(
        EventId = 5004,
        Level = LogLevel.Warning,
        Message = "AI ranking failed for model {Model}; the model was unavailable")]
    public static partial void RankFailed(ILogger logger, Exception exception, string model);
}
