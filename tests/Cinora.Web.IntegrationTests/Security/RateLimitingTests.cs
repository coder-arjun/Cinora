using System.Globalization;
using System.Net;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Cinora.Web.IntegrationTests.Security;

/// <summary>
/// Verifies Milestone 1.6b / F3: the <c>"auth"</c> rate-limiting policy genuinely throttles the auth
/// endpoints. The permit is relaxed to a very high value in Testing so the rest of the suite is never
/// affected; this test spins up a factory variant that overrides the permit to a LOW value via
/// configuration (<c>RateLimiting:AuthPermitLimit</c>) — its own fresh host means a fresh limiter
/// partition — and proves the first admitted requests succeed while the next is rejected with 429.
/// Shares the single base factory via the non-parallel collection so hosts never race.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class RateLimitingTests
{
    private const string LoginPath = "/account/login";
    private const int TestPermitLimit = 3;

    private readonly CinoraWebApplicationFactory _factory;

    public RateLimitingTests(CinoraWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Auth_endpoint_returns_429_once_the_permit_limit_is_exhausted()
    {
        // A derived host with a deliberately low auth permit; the base factory's process-wide connection
        // string + "Testing" environment still apply, so nothing else about the app changes.
        using var strictFactory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimiting:AuthPermitLimit"] = TestPermitLimit.ToString(CultureInfo.InvariantCulture),
                })));

        using var client = strictFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

        // The rate limiter runs as endpoint middleware BEFORE the anti-forgery filter, so these tokenless
        // POSTs (which would otherwise be rejected with 400) still each consume one "auth" permit. The
        // first TestPermitLimit requests are admitted; the very next one is throttled.
        var statusCodes = new List<HttpStatusCode>();
        for (var i = 0; i < TestPermitLimit + 1; i++)
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>());
            using var response = await client.PostAsync(LoginPath, content);
            statusCodes.Add(response.StatusCode);
        }

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statusCodes.Take(TestPermitLimit));
        Assert.Equal(HttpStatusCode.TooManyRequests, statusCodes[^1]);
    }
}
