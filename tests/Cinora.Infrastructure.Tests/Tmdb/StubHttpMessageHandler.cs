using System.Net;
using System.Text;

namespace Cinora.Infrastructure.Tests.Tmdb;

/// <summary>
/// A fake <see cref="HttpMessageHandler"/> that returns canned responses for the TMDB adapter tests — no
/// real network. A responder function maps the zero-based call index to a response, so a single call can
/// return a fixture while a sequence can model a transient failure followed by success (resilience test).
/// It also records the last request URI and the call count so tests can assert URL construction and retries.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<int, HttpResponseMessage> _responder;

    /// <summary>Creates a handler whose response is produced by <paramref name="responder"/> per call index.</summary>
    /// <param name="responder">Maps the zero-based invocation index to the response to return.</param>
    public StubHttpMessageHandler(Func<int, HttpResponseMessage> responder) => _responder = responder;

    /// <summary>The URI of the most recent request, for asserting URL construction.</summary>
    public Uri? LastRequestUri { get; private set; }

    /// <summary>The number of times the handler has been invoked, for asserting retries.</summary>
    public int CallCount { get; private set; }

    /// <summary>Creates a handler that returns the same JSON response for every call.</summary>
    /// <param name="status">The HTTP status code to return.</param>
    /// <param name="json">The JSON body to return.</param>
    /// <returns>The configured handler.</returns>
    public static StubHttpMessageHandler Json(HttpStatusCode status, string json) =>
        new(_ => JsonResponse(status, json));

    /// <summary>Builds a JSON <see cref="HttpResponseMessage"/> with the given status and body.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="json">The JSON body.</param>
    /// <returns>The response message.</returns>
    public static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        LastRequestUri = request.RequestUri;
        var index = CallCount;
        CallCount++;
        return Task.FromResult(_responder(index));
    }
}
