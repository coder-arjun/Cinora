using Cinora.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ValidationException = Cinora.Application.Common.Exceptions.ValidationException;

namespace Cinora.Web.Infrastructure;

/// <summary>
/// The single <see cref="IExceptionHandler"/> that translates every unhandled exception into an RFC 7807
/// <c>ProblemDetails</c> response. Expected exceptions map to precise status codes:
/// the Application <see cref="ValidationException"/> → 400 <see cref="ValidationProblemDetails"/> (carrying
/// the field errors), <see cref="Cinora.Application.Common.Exceptions.NotFoundException"/> → 404, and
/// <see cref="DomainException"/> → 400 (backlog item M2). Anything else is a 500 whose body carries only a
/// generic title and detail — never a stack trace — while the full exception is logged server-side. It is
/// registered in all environments so clients (and the integration tests) always receive ProblemDetails
/// rather than a developer error page.
/// </summary>
internal sealed partial class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    /// <summary>Attempts to translate <paramref name="exception"/> into a ProblemDetails response.</summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <param name="exception">The unhandled exception to translate.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns><see langword="true"/> when the response was written; otherwise <see langword="false"/>.</returns>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var problemDetails = exception switch
        {
            ValidationException validationException => CreateValidationProblem(validationException),
            Cinora.Application.Common.Exceptions.NotFoundException => new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Resource not found",
                Detail = exception.Message,
            },
            Cinora.Application.Common.Exceptions.ForbiddenAccessException => new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden",
                Detail = exception.Message,
            },
            DomainException => new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Request could not be processed",
                Detail = exception.Message,
            },
            _ => new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred",
                Detail = "An unexpected error occurred while processing your request.",
            },
        };

        var status = problemDetails.Status ?? StatusCodes.Status500InternalServerError;

        // 500s are genuine faults — log the full exception (server-side only). Expected 4xx exceptions are
        // anomalies, not faults: log at Warning with identifiers only, never the exception body to clients.
        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogUnhandledException(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            LogHandledException(logger, status, problemDetails.Title, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;

        // Note: the raw Exception is deliberately NOT placed on the context, so no stack trace can leak into
        // the serialized body. The ProblemDetails object above is exactly what the client receives.
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
        });
    }

    private static ValidationProblemDetails CreateValidationProblem(ValidationException exception) =>
        new(exception.Errors.ToDictionary(entry => entry.Key, entry => entry.Value))
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred.",
        };

    [LoggerMessage(
        EventId = 5000,
        Level = LogLevel.Error,
        Message = "Unhandled exception for {Method} {Path}")]
    private static partial void LogUnhandledException(
        ILogger logger,
        Exception exception,
        string method,
        string path);

    [LoggerMessage(
        EventId = 4000,
        Level = LogLevel.Warning,
        Message = "Request failed with {StatusCode} ({Title}) for {Path}")]
    private static partial void LogHandledException(
        ILogger logger,
        int statusCode,
        string? title,
        string path);
}
