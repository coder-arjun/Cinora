using System.Net;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Cinora.Web.IntegrationTests.Health;

/// <summary>
/// Verifies Milestone 1.7: the <c>/health</c> endpoint is reachable anonymously (proving its
/// <c>.AllowAnonymous()</c> opt-out from the fail-closed global authorization policy) and returns
/// <c>200 Healthy</c> when the database is reachable. The check exercises
/// <see cref="Cinora.Web.Infrastructure.DatabaseHealthCheck"/> against the migrated <c>CinoraTest</c>
/// LocalDB, so the shared factory's schema build is awaited first. Uses the non-parallel collection so
/// it reuses the single factory alongside the other integration suites.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class HealthCheckTests : IAsyncLifetime
{
    private const string HealthPath = "/health";

    private readonly CinoraWebApplicationFactory _factory;

    public HealthCheckTests(CinoraWebApplicationFactory factory) => _factory = factory;

    // The health check probes real DB connectivity, so the CinoraTest schema must exist first.
    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Get_health_anonymously_returns_200_healthy()
    {
        using var client = CreateClient();

        // No auth cookie is attached: a 200 here proves the endpoint is [AllowAnonymous] under the
        // fail-closed fallback policy (an authorization redirect/401 would fail this assertion).
        using var response = await client.GetAsync(HealthPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("Healthy", body);
    }

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
}
