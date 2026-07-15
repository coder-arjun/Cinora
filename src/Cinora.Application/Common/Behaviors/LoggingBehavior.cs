using System.Diagnostics;
using Cinora.Application.Common.Messaging;
using Microsoft.Extensions.Logging;

namespace Cinora.Application.Common.Behaviors;

/// <summary>
/// The outermost pipeline behavior: emits one structured event per request recording its type name,
/// outcome (completed or failed), and elapsed milliseconds. It logs identifiers and timings only — never
/// request contents — so no PII or secrets reach the log. Exceptions are logged at Warning and rethrown
/// so the global exception handler still performs the final translation and error logging.
/// </summary>
/// <typeparam name="TRequest">The request being handled.</typeparam>
/// <typeparam name="TResponse">The response the request produces.</typeparam>
/// <param name="logger">The logger, categorized by the request type.</param>
public sealed class LoggingBehavior<TRequest, TResponse>(
    ILogger<TRequest> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var response = await next(cancellationToken);
            stopwatch.Stop();

            PipelineLog.RequestCompleted(logger, requestName, stopwatch.ElapsedMilliseconds);

            return response;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();

            PipelineLog.RequestFailed(logger, exception, requestName, stopwatch.ElapsedMilliseconds);

            throw;
        }
    }
}
