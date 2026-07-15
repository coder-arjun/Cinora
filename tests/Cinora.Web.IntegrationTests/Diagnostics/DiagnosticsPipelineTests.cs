using System.Net;
using System.Net.Http.Headers;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Serilog.Events;

namespace Cinora.Web.IntegrationTests.Diagnostics;

/// <summary>
/// Exercises Milestone 1.4 through the real HTTP stack: the hand-rolled mediator pipeline (validation
/// behavior), the global exception handler's ProblemDetails mapping (400 / 404 / 400-for-DomainException /
/// 500 with no stack trace), and Serilog request logging. Drives the non-production, <c>[AllowAnonymous]</c>
/// diagnostics endpoints. Shares the single factory with the auth suite via the non-parallel collection.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class DiagnosticsPipelineTests
{
    private const string PingPath = "/diagnostics/ping";
    private const string ThrowPath = "/diagnostics/throw";
    private const string DomainErrorPath = "/diagnostics/domain-error";
    private const string MissingPath = "/diagnostics/missing";
    private const string ProblemJson = "application/problem+json";

    private readonly CinoraWebApplicationFactory _factory;

    public DiagnosticsPipelineTests(CinoraWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Ping_with_a_valid_message_returns_200_from_the_handler_through_the_pipeline()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync($"{PingPath}?message=hello");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("pong: hello", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ping_with_an_empty_message_returns_400_validation_problem_details_with_the_errors()
    {
        using var client = CreateClient();

        // Empty message fails the PingCommand validator inside the ValidationBehavior, which throws the
        // Application ValidationException before the handler runs; the global handler maps it to 400.
        using var response = await client.GetAsync($"{PingPath}?message=");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"errors\"", body, StringComparison.Ordinal);
        Assert.Contains("Message", body, StringComparison.Ordinal);
        Assert.Contains("Message is required.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forced_unhandled_exception_returns_500_problem_details_without_a_stack_trace()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync(ThrowPath);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();

        // The generic title is present; no exception type, no internal message, no stack frames leak.
        Assert.Contains("An unexpected error occurred", body, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Forced diagnostics failure", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DiagnosticsController", body, StringComparison.Ordinal);
        Assert.DoesNotContain("at Cinora", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DomainException_maps_to_400_problem_details()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync(DomainErrorPath);

        // Backlog item M2: a Domain invariant violation surfaces as 400, not 500.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("DomainException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotFoundException_maps_to_404_problem_details()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync(MissingPath);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_request_emits_a_serilog_request_logging_event()
    {
        using var client = CreateClient();
        _factory.LogSink.Clear();

        using var response = await client.GetAsync($"{PingPath}?message=serilog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // UseSerilogRequestLogging writes one completion event per request carrying RequestMethod +
        // StatusCode. Poll briefly because the event is written as the response completes server-side.
        var found = await WaitForRequestLogEventAsync();

        Assert.True(found, "Expected a Serilog request-logging event (RequestMethod + StatusCode) to be emitted.");
    }

    [Fact]
    public async Task A_validation_400_logs_the_request_completion_at_a_non_error_level_with_status_400()
    {
        using var client = CreateClient();
        _factory.LogSink.Clear();

        // Empty message → 400 ValidationProblemDetails. Because UseSerilogRequestLogging is now OUTERMOST
        // (before UseExceptionHandler), the request-completion event records the FINAL translated status —
        // not the in-flight exception — so it must be StatusCode 400 at a non-error level with no exception
        // attached. This locks in the request-logging/exception-handler ordering fix.
        using var response = await client.GetAsync($"{PingPath}?message=");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var completionEvent = await WaitForRequestCompletionEventAsync(expectedStatusCode: 400);

        Assert.NotNull(completionEvent);
        Assert.NotEqual(LogEventLevel.Error, completionEvent!.Level);
        Assert.NotEqual(LogEventLevel.Fatal, completionEvent.Level);
        Assert.Null(completionEvent.Exception);
    }

    private async Task<LogEvent?> WaitForRequestCompletionEventAsync(int expectedStatusCode)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var completionEvent = _factory.LogSink.Snapshot().FirstOrDefault(logEvent =>
                logEvent.Properties.ContainsKey("RequestMethod")
                && TryGetStatusCode(logEvent, out var statusCode)
                && statusCode == expectedStatusCode);

            if (completionEvent is not null)
            {
                return completionEvent;
            }

            await Task.Delay(25);
        }

        return null;
    }

    private static bool TryGetStatusCode(LogEvent logEvent, out int statusCode)
    {
        if (logEvent.Properties.TryGetValue("StatusCode", out var value)
            && value is ScalarValue { Value: int code })
        {
            statusCode = code;
            return true;
        }

        statusCode = 0;
        return false;
    }

    private async Task<bool> WaitForRequestLogEventAsync()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var hasRequestLogEvent = _factory.LogSink.Snapshot().Any(logEvent =>
                logEvent.Properties.ContainsKey("RequestMethod")
                && logEvent.Properties.ContainsKey("StatusCode"));

            if (hasRequestLogEvent)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

        // Ask for JSON so the ProblemDetails writer negotiates application/problem+json.
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
