using System.Net;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Cinora.Web.IntegrationTests.Jobs;

/// <summary>
/// Milestone 5.3 fail-closed test for the admin-gated Hangfire <c>/jobs</c> dashboard (ADR 0018 §5). The dashboard
/// is terminal Hangfire middleware whose OWN <c>AdminDashboardAuthorizationFilter</c> is the sole gate — with no
/// admins configured (the Testing default) an anonymous request is DENIED. Hangfire returns <c>401 Unauthorized</c>
/// (NOT a 302 login redirect like a cookie-auth MVC route), decided by the filter BEFORE any Hangfire storage
/// access, so it works even though the Testing host leaves the Hangfire schema unprepared. The pure
/// <see cref="Cinora.Web.Security.AdminAccessPolicy"/> unit tests are the guaranteed evidence; this exercises the
/// same decision through the real HTTP + middleware pipeline.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class HangfireDashboardTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public HangfireDashboardTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Anonymous_request_to_the_jobs_dashboard_is_denied_with_401_not_a_login_redirect()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

        using var response = await client.GetAsync("/jobs");

        // Fail-closed: the dashboard's own authorization filter denies the anonymous caller. Hangfire responds
        // 401 (not a 302 to /account/login), and does so before touching the (unprepared) Hangfire storage.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
