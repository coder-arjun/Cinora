using System.Net;
using Cinora.Web.IntegrationTests.Infrastructure;

namespace Cinora.Web.IntegrationTests.Security;

/// <summary>
/// Task B4.1 (ADR 0023) — Cinora is login-only. Every content route requires authentication (an anonymous
/// request is redirected to login), while the public allowlist — the landing page, the auth pages, and the
/// anonymous <c>/health</c> probe — stays reachable without signing in.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class AnonymousLockdownTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public AnonymousLockdownTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("/discover")]
    [InlineData("/watchlist")]
    [InlineData("/recommendations")]
    public async Task Anonymous_content_route_redirects_to_login(string path)
    {
        using var client = TestAuthentication.CreateClient(_factory); // anonymous, does not auto-follow redirects
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/account/login",
            response.Headers.Location?.OriginalString ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/")]           // the marketing landing (public entry)
    [InlineData("/health")]     // the anonymous DB-connectivity probe
    [InlineData("/account/login")]
    public async Task Public_allowlist_route_is_reachable_anonymously(string path)
    {
        using var client = TestAuthentication.CreateClient(_factory);
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
