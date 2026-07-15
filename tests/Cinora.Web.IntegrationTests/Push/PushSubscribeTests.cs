using System.Net;
using System.Text;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Web.IntegrationTests.Push;

/// <summary>
/// Milestone 6.2 (ADR 0020) integration tests for the Web Push subscribe endpoint, driven end-to-end through the
/// real MVC + hand-rolled <c>ISender</c> + Identity pipeline against the migrated <c>CinoraTest</c> LocalDB. They
/// prove the fail-closed + anti-forgery contract (anonymous → login redirect; authed-without-token → 400), that a
/// valid subscribe lands as a <see cref="Cinora.Domain.Entities.Device"/> for the server-resolved current user,
/// that a second identical subscribe upserts (still ONE device), and that the endpoints do not widen the strict
/// CSP. Each test partitions its endpoints/users so the shared database stays isolated.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class PushSubscribeTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public PushSubscribeTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Subscribe_anonymous_is_redirected_to_login()
    {
        using var client = TestAuthentication.CreateClient(_factory);

        using var response = await PostSubscriptionAsync(client, UniqueEndpoint(), token: null);

        // Fail-closed: the AuthorizationMiddleware challenges the anonymous request before the action runs.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/account/login", response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Subscribe_authenticated_without_antiforgery_token_is_rejected()
    {
        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "Push Subscriber");

        using var response = await PostSubscriptionAsync(user.Client, UniqueEndpoint(), token: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Subscribe_authenticated_with_token_persists_a_device_for_the_current_user()
    {
        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "Push Subscriber");
        var endpoint = UniqueEndpoint();
        var token = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/discover");

        using var response = await PostSubscriptionAsync(user.Client, endpoint, token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var devices = await DevicesForAsync(user.UserId);
        var device = Assert.Single(devices);
        Assert.Equal(endpoint, device.Endpoint);
        Assert.Equal("p256dh-value", device.P256dhKey);
        Assert.Equal("auth-value", device.AuthSecret);
    }

    [Fact]
    public async Task Subscribe_twice_with_the_same_endpoint_upserts_to_one_device()
    {
        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "Push Subscriber");
        var endpoint = UniqueEndpoint();

        var token1 = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/discover");
        using (var first = await PostSubscriptionAsync(user.Client, endpoint, token1))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }

        var token2 = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/discover");
        using (var second = await PostSubscriptionAsync(user.Client, endpoint, token2))
        {
            Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        }

        var devices = await DevicesForAsync(user.UserId);
        Assert.Single(devices); // upsert — the same endpoint did NOT create a second row.
    }

    [Fact]
    public async Task Push_public_key_endpoint_carries_the_unchanged_strict_csp()
    {
        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "Push CSP User");

        using var response = await user.Client.GetAsync("/push/public-key");

        Assert.True(
            response.Headers.TryGetValues("Content-Security-Policy", out var values),
            "The /push endpoints must carry the strict CSP header (SecurityHeadersMiddleware is unchanged).");
        var csp = Assert.Single(values!);

        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
    }

    // POST /push/test — fail-closed: an anonymous caller is challenged to login before the action runs.
    [Fact]
    public async Task Test_push_anonymous_is_redirected_to_login()
    {
        using var client = TestAuthentication.CreateClient(_factory);

        using var response = await PostTestAsync(client, token: null);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/account/login", response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // POST /push/test without the anti-forgery token is rejected (global AutoValidateAntiforgeryToken).
    [Fact]
    public async Task Test_push_authenticated_without_antiforgery_token_is_rejected()
    {
        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "Push Tester");

        using var response = await PostTestAsync(user.Client, token: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // POST /push/test (authed + token) returns 200 with a friendly JSON { message } — whether push is configured,
    // unconfigured, or the user has no device yet, it never faults; the opt-in bar's "Send test" shows the message.
    [Fact]
    public async Task Test_push_authenticated_with_token_returns_a_friendly_message()
    {
        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "Push Tester");
        var token = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/discover");

        using var response = await PostTestAsync(user.Client, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("message", body, StringComparison.Ordinal);
    }

    private static string UniqueEndpoint() => $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";

    private static async Task<HttpResponseMessage> PostTestAsync(HttpClient client, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/push/test");
        if (token is not null)
        {
            request.Headers.Add("RequestVerificationToken", token);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostSubscriptionAsync(
        HttpClient client, string endpoint, string? token)
    {
        var json =
            "{\"endpoint\":\"" + endpoint + "\",\"expirationTime\":null," +
            "\"keys\":{\"p256dh\":\"p256dh-value\",\"auth\":\"auth-value\"}}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/push/subscribe")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (token is not null)
        {
            request.Headers.Add("RequestVerificationToken", token);
        }

        return await client.SendAsync(request);
    }

    private async Task<List<Cinora.Domain.Entities.Device>> DevicesForAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Devices.AsNoTracking().Where(device => device.UserId == userId).ToListAsync();
    }
}
