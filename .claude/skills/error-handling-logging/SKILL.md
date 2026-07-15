---
name: error-handling-logging
description: Use when handling exceptions, shaping ProblemDetails responses, configuring Serilog, deciding log levels, or reviewing code with try/catch blocks or sensitive data in log statements in Cinora.
---

# Error Handling & Logging

## Overview
One global `IExceptionHandler` translates exceptions into ProblemDetails; Serilog records structured events with message templates. Handlers and controllers stay free of try/catch.

## Quick Reference
| Task | Approach |
|---|---|
| Global handling | Single `IExceptionHandler` implementation + `AddProblemDetails()` + `UseExceptionHandler()` first in the pipeline |
| Validation failures | `ValidationException` → 400 `ValidationProblemDetails` (see `.claude/skills/fluent-validation/SKILL.md`) |
| Missing entity | `NotFoundException` → 404 ProblemDetails |
| Unknown exception | 500, generic message to client, full exception to logs only |
| Serilog setup | `builder.Host.UseSerilog(...)`; `app.UseSerilogRequestLogging()` replaces per-request noise |
| Enrichment | `FromLogContext`, environment/app name, correlation/trace Id on every event |
| Message style | Templates with named properties: `LogInformation("Review {ReviewId} created by {UserId}", ...)` |
| Never log | Passwords, tokens, API keys, cookies, full email addresses, request bodies with PII |

## Pattern
```csharp
// Cinora.Web/Infrastructure/GlobalExceptionHandler.cs
public class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken ct)
    {
        // WHY: expected exceptions map to precise status codes;
        // everything else is a 500 with details kept server-side.
        (int status, string title) = exception switch
        {
            ValidationException => (StatusCodes.Status400BadRequest, "Validation failed"),
            NotFoundException => (StatusCodes.Status404NotFound, "Resource not found"),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred")
        };

        if (status == StatusCodes.Status500InternalServerError)
            logger.LogError(exception, "Unhandled exception for {Method} {Path}",
                httpContext.Request.Method, httpContext.Request.Path); // WHY: structured props, no string interpolation
        else
            logger.LogWarning("Request failed with {StatusCode}: {Title}", status, title);

        httpContext.Response.StatusCode = status;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title }
        });
    }
}

// Program.cs wiring:
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
// ...
app.UseExceptionHandler(); // first middleware
app.UseSerilogRequestLogging(); // one enriched event per request
```

## Log Levels Policy
- **Error**: unhandled exceptions, failed external calls after retries.
- **Warning**: handled anomalies — validation failures, 404s, slow requests, retry attempts.
- **Information**: business milestones — review created, friend request accepted. Sparse.
- **Debug**: developer diagnostics; disabled in production configuration.

For HTMX form flows, controllers may still catch `ValidationException` locally to re-render the form with `ModelState`; everything else flows to the global handler. See `.claude/skills/aspnet-core-mvc/SKILL.md`.

## Common Mistakes
| Mistake | Fix |
|---|---|
| try/catch-log-rethrow in every handler | Delete them; the global handler logs once |
| String interpolation in log calls (`$"user {id}"`) | Message templates — keeps events queryable |
| Exception details or stack traces in 500 responses | Generic ProblemDetails to clients; details in logs |
| Logging tokens, keys, or PII "temporarily" | Never; log identifiers (UserId), not identities |
| Catching `Exception` to swallow it | Catch only what you can handle meaningfully |
| Multiple competing error middlewares | Exactly one `IExceptionHandler` owns translation |
