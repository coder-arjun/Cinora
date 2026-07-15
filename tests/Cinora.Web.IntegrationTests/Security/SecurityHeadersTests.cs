using System.Net;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Cinora.Web.IntegrationTests.Security;

/// <summary>
/// Verifies Milestone 1.6b / F2: every normal response carries the baseline OWASP security headers
/// (<c>X-Content-Type-Options</c>, <c>Referrer-Policy</c>, <c>Content-Security-Policy</c>,
/// <c>Permissions-Policy</c>) written by <see cref="Cinora.Web.Infrastructure.SecurityHeadersMiddleware"/>.
/// Drives the anonymous landing page (a 200 that needs no database), and confirms the strict CSP is the
/// same-origin policy that keeps the app's own hashed <c>/dist/</c> assets loadable. Shares the single
/// factory via the non-parallel collection.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class SecurityHeadersTests
{
    private readonly CinoraWebApplicationFactory _factory;

    public SecurityHeadersTests(CinoraWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Normal_response_carries_the_baseline_security_headers()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal("nosniff", SingleHeaderValue(response, "X-Content-Type-Options"));
        Assert.Equal("strict-origin-when-cross-origin", SingleHeaderValue(response, "Referrer-Policy"));

        // CSP is same-origin only: 'self' keeps the app's own hashed /dist/ CSS+JS (and same-origin HTMX
        // fetches) loadable, while frame-ancestors/object-src 'none' close clickjacking and plugin vectors.
        var csp = SingleHeaderValue(response, "Content-Security-Policy");
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("style-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self'", csp, StringComparison.Ordinal);

        // Phase 2 (Milestone 2.3): img-src is widened to allow TMDB poster/backdrop/profile art (data: kept
        // for the skeleton placeholder). Every other directive stays strict; script-src must keep NO eval.
        Assert.Contains("img-src 'self' https://image.tmdb.org https://images.weserv.nl data:", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("base-uri 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("form-action 'self'", csp, StringComparison.Ordinal);

        // The powerful features Cinora never uses are denied.
        var permissions = SingleHeaderValue(response, "Permissions-Policy");
        Assert.Contains("camera=()", permissions, StringComparison.Ordinal);
        Assert.Contains("microphone=()", permissions, StringComparison.Ordinal);
        Assert.Contains("geolocation=()", permissions, StringComparison.Ordinal);
    }

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private static string SingleHeaderValue(HttpResponseMessage response, string name)
    {
        Assert.True(
            response.Headers.TryGetValues(name, out var values),
            $"Expected the response header '{name}' to be present.");
        return Assert.Single(values!);
    }
}
