using System.Net;
using Cinora.Web.IntegrationTests.Infrastructure;

namespace Cinora.Web.IntegrationTests.Friends;

/// <summary>
/// HTTP-driven enforcement test for user blocking (Milestone B1, §2/§7), mirroring
/// <see cref="FriendProfileTests"/>'s helpers. The handler-level block/unblock/full-cut-off logic is already
/// covered by <c>BlockEnforcementHandlerTests</c>; this suite proves the wiring end to end through the real MVC
/// + anti-forgery + Identity pipeline: the <c>POST /friends/{userId}/block</c> endpoint (Task B1.5) plus the
/// existing <c>GetProfileQuery</c> block resolution (Task B1.4) — the blocked user's view of the blocker 404s
/// (no leak, same as a missing user) and the blocker's view of the blocked user renders the Unblock control.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class BlockEnforcementTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public BlockEnforcementTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // Blocking is silent and full-cut-off (§2, §9): once the blocker blocks, the blocked user cannot see the
    // blocker's profile at all (404 — indistinguishable from a non-existent user), while the blocker sees the
    // blocked user's profile collapsed to the minimal Unblock-only state.
    [Fact]
    public async Task Blocked_user_gets_404_on_the_blockers_profile_and_blocker_sees_unblock()
    {
        using var blocker = await TestAuthentication.RegisterAndSignInAsync(_factory, "Blocker Bea");
        using var blocked = await TestAuthentication.RegisterAndSignInAsync(_factory, "Blocked Bud");

        await BlockAsync(blocker.Client, blocked.UserId);

        // The blocked user cannot see the blocker's profile at all (same 404 as a missing user — no leak).
        using var blockedView = await blocked.Client.GetAsync($"/users/{blocker.UserId}");
        Assert.Equal(HttpStatusCode.NotFound, blockedView.StatusCode);

        // The blocker viewing the blocked user's profile sees the "Unblock" affordance, not their content.
        using var blockerView = await blocker.Client.GetAsync($"/users/{blocked.UserId}");
        Assert.Equal(HttpStatusCode.OK, blockerView.StatusCode);
        Assert.Contains("Unblock", await blockerView.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    // Issues the authenticated POST /friends/{targetUserId}/block request, carrying the anti-forgery token via
    // the RequestVerificationToken header and HX-Request: true (mirrors FriendProfileTests' HTTP helper pattern).
    private static async Task BlockAsync(HttpClient client, Guid targetUserId)
    {
        var token = await TestAuthentication.AntiforgeryTokenAsync(client, "/friends");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/friends/{targetUserId}/block");
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("HX-Request", "true");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
