using Microsoft.Extensions.Logging;

namespace Cinora.Application.Common.Behaviors;

/// <summary>
/// Source-generated, high-performance log messages for the mediator pipeline behaviors. Using
/// <see cref="LoggerMessageAttribute"/> avoids boxing and evaluates arguments only when the level is
/// enabled. These are defined in a non-generic type because the logging source generator does not emit
/// methods for generic containing types; the generic behaviors pass their <see cref="ILogger"/> here.
/// </summary>
internal static partial class PipelineLog
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Request {RequestName} completed in {ElapsedMilliseconds}ms")]
    public static partial void RequestCompleted(ILogger logger, string requestName, long elapsedMilliseconds);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Request {RequestName} failed after {ElapsedMilliseconds}ms")]
    public static partial void RequestFailed(
        ILogger logger,
        Exception exception,
        string requestName,
        long elapsedMilliseconds);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "Slow request {RequestName} took {ElapsedMilliseconds}ms (threshold {ThresholdMilliseconds}ms)")]
    public static partial void SlowRequest(
        ILogger logger,
        string requestName,
        long elapsedMilliseconds,
        long thresholdMilliseconds);
}
