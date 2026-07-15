using System.Diagnostics;
using Cinora.Application.Common.Messaging;
using Microsoft.Extensions.Logging;

namespace Cinora.Application.Common.Behaviors;

/// <summary>
/// The innermost pipeline behavior (closest to the handler): times the handler and logs a Warning when it
/// exceeds <see cref="ThresholdMilliseconds"/>, surfacing slow requests without adding cost to fast ones.
/// It records only the request name and elapsed time — never request contents.
/// </summary>
/// <typeparam name="TRequest">The request being handled.</typeparam>
/// <typeparam name="TResponse">The response the request produces.</typeparam>
/// <param name="logger">The logger, categorized by the request type.</param>
public sealed class PerformanceBehavior<TRequest, TResponse>(
    ILogger<TRequest> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    /// <summary>The elapsed-time threshold, in milliseconds, above which a request is logged as slow.</summary>
    public const long ThresholdMilliseconds = 500;

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = await next(cancellationToken);
        stopwatch.Stop();

        if (stopwatch.ElapsedMilliseconds > ThresholdMilliseconds)
        {
            PipelineLog.SlowRequest(
                logger,
                typeof(TRequest).Name,
                stopwatch.ElapsedMilliseconds,
                ThresholdMilliseconds);
        }

        return response;
    }
}
