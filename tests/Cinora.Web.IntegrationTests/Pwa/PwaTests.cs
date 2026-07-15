using System.Net;
using System.Text.Json;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Cinora.Web.IntegrationTests.Pwa;

/// <summary>
/// Milestone 6.1 (ADR 0019) integration tests for the PWA foundation, driven end-to-end through the real
/// pipeline against the migrated <c>CinoraTest</c> LocalDB. They prove the automatable slice: the branded
/// public <c>/offline</c> fallback (anonymous-reachable), the web app manifest (content type + <c>no-cache</c>
/// + valid installable JSON), the service worker (<c>/sw.js</c>, JS content type + <c>no-cache</c>), the
/// privacy header (<c>no-store</c> on an authenticated page but NOT on an anonymous one), and that the strict
/// CSP is unchanged — the new <c>/offline</c> page is covered by the same policy with no directive widened.
/// The manual DevTools verifications (installability, SW scope, real offline, update toast) are listed in the
/// milestone report as the 6.1 manual smoke; they need a real browser and are out of scope here.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class PwaTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public PwaTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Offline_page_is_anonymously_reachable_and_branded()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync("/offline");

        // Anonymously reachable (no auth redirect) and a real HTML page.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("offline", html, StringComparison.OrdinalIgnoreCase); // branded copy
        Assert.Contains("Try again", html, StringComparison.Ordinal); // the retry affordance
    }

    [Fact]
    public async Task Offline_page_is_public_and_cacheable_not_no_store()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync("/offline");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // /offline must stay cacheable (the SW precaches it) — cacheable-with-revalidation, never no-store.
        Assert.NotEqual(true, response.Headers.CacheControl?.NoStore);
        Assert.Equal(true, response.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task Manifest_is_served_with_manifest_content_type_no_cache_and_valid_json()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync("/manifest.webmanifest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(true, response.Headers.CacheControl?.NoCache);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json); // valid JSON (throws otherwise)
        var root = doc.RootElement;

        Assert.Equal("Cinora", root.GetProperty("name").GetString());
        Assert.Equal("/", root.GetProperty("start_url").GetString());
        Assert.Equal("standalone", root.GetProperty("display").GetString());

        var icons = root.GetProperty("icons");
        var sizes = new List<string>();
        var purposes = new List<string>();
        foreach (var icon in icons.EnumerateArray())
        {
            sizes.Add(icon.GetProperty("sizes").GetString() ?? string.Empty);
            purposes.Add(icon.GetProperty("purpose").GetString() ?? string.Empty);
        }

        Assert.Contains("192x192", sizes);
        Assert.Contains("512x512", sizes);
        Assert.Contains("maskable", purposes); // the maskable variant for adaptive icons
    }

    [Fact]
    public async Task Service_worker_is_served_with_js_content_type_and_no_cache()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync("/sw.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        Assert.Contains("javascript", contentType, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(true, response.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task Authenticated_html_page_carries_no_store()
    {
        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "PWA Privacy User");

        using var response = await user.Client.GetAsync("/home");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Privacy (ADR 0019 §3): the SW must never cache one user's personalized feed on a shared device.
        Assert.Equal(true, response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Anonymous_public_page_is_cacheable_not_no_store()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync("/"); // the anonymous landing page

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // A public page stays cacheable so the SW can serve it offline once visited — the app relaxes the
        // anti-forgery blanket no-store to no-cache for anonymous public GETs; no-store is authenticated-only.
        Assert.NotEqual(true, response.Headers.CacheControl?.NoStore);
        Assert.Equal(true, response.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task Account_page_stays_no_store_not_relaxed_to_no_cache()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync("/account/login"); // an anti-forgery-token account page

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Account pages (login/register/denied + external-login callbacks) are NOT in the offline scope
        // (design §2.3); the anonymous-GET no-cache relaxation must not reach them — they stay no-store so the
        // SW never caches a token-rendering page. Regression-locks the scope carve-out.
        Assert.Equal(true, response.Headers.CacheControl?.NoStore);
        Assert.NotEqual(true, response.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task Offline_page_is_covered_by_the_unchanged_strict_csp()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync("/offline");

        Assert.True(
            response.Headers.TryGetValues("Content-Security-Policy", out var values),
            "The /offline page must carry the strict CSP header (SecurityHeadersMiddleware is unchanged).");
        var csp = Assert.Single(values!);

        // Exactly the Phase-1/2 policy — no directive widened for the PWA (design §4).
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
    }

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
}
