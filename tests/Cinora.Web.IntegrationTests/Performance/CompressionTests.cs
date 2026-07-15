using System.Net;
using Cinora.Web.IntegrationTests.Infrastructure;
using Cinora.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Web.IntegrationTests.Performance;

/// <summary>
/// Milestone 6.4 (§6.1, ADR 0021) — response-compression tests. They lock the BREACH-safe scope end-to-end
/// through the real pipeline: a fingerprinted <c>/dist/*</c> static asset is Brotli-compressed with a
/// <c>Vary: Accept-Encoding</c> and cached immutably, while a dynamic HTML page is NEVER compressed (an HTML
/// response carries the rotating anti-forgery token AND reflected user input — the classic BREACH exposure). The
/// strict CSP still stamps the compressed asset (SecurityHeadersMiddleware stays UPSTREAM of compression). Shares
/// the single factory via the non-parallel collection.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class CompressionTests
{
    private readonly CinoraWebApplicationFactory _factory;

    public CompressionTests(CinoraWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Static_dist_js_is_compressed_with_vary_immutable_cache_and_the_unchanged_csp()
    {
        using var client = CreateClient();

        // Resolve the hashed bundle URL the same way the layout does (IAssetManifest → /dist/site-<hash>.js).
        var assetUrl = _factory.Services.GetRequiredService<IAssetManifest>().Resolve("site.js");

        using var request = new HttpRequestMessage(HttpMethod.Get, assetUrl);
        request.Headers.AcceptEncoding.ParseAdd("br");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Compressed (Brotli) with the content-negotiation Vary header.
        Assert.Contains("br", response.Content.Headers.ContentEncoding);
        Assert.Contains("Accept-Encoding", response.Headers.Vary);

        // Content-hashed → immutable long-lived caching (Milestone 6.4 §6.1 code-gate).
        Assert.True(
            response.Headers.TryGetValues("Cache-Control", out var cacheControl),
            "The /dist/* asset must carry a Cache-Control header.");
        var cache = Assert.Single(cacheControl!);
        Assert.Contains("max-age=31536000", cache, StringComparison.Ordinal);
        Assert.Contains("immutable", cache, StringComparison.Ordinal);

        // SecurityHeadersMiddleware stays upstream of compression → the compressed asset still carries the CSP.
        Assert.True(
            response.Headers.Contains("Content-Security-Policy"),
            "The compressed static asset must still carry the strict CSP (SecurityHeadersMiddleware is upstream).");
    }

    [Fact]
    public async Task Static_dist_css_is_also_compressed()
    {
        using var client = CreateClient();
        var assetUrl = _factory.Services.GetRequiredService<IAssetManifest>().Resolve("app.css");

        using var request = new HttpRequestMessage(HttpMethod.Get, assetUrl);
        request.Headers.AcceptEncoding.ParseAdd("br");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("br", response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task Html_page_is_never_compressed_breach_safety()
    {
        using var client = CreateClient();

        // The anonymous landing page is text/html — deliberately OUT of the MimeTypes allow-list so a compressed
        // HTML body can never carry both the anti-forgery token and reflected user input (BREACH). Even though the
        // client advertises br + gzip, the response must be uncompressed.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.AcceptEncoding.ParseAdd("br");
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(response.Content.Headers.ContentEncoding); // never compressed — no Content-Encoding at all
    }

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
}
