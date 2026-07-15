using System.Net;
using Cinora.Web.IntegrationTests.Infrastructure;

namespace Cinora.Web.IntegrationTests.Friends;

/// <summary>
/// Task B2.3 HTTP-level tests for the Find-people search endpoint (<c>GET /friends/search?term=</c>). The
/// handler itself (exact match, self/blocked exclusion) is covered by <see cref="UserSearchTests"/>; here the
/// full MVC pipeline is exercised end-to-end — the controller action, <c>_UserSearchResult</c> partial
/// rendering, and rate-limiting registration — via a real registered/signed-in client
/// (<see cref="TestAuthentication.RegisterAndSignInAsync"/>), mirroring <see cref="FriendProfileTests"/>'s
/// authenticated GET pattern. Both a match and a no-match return <c>200</c> (never an error) so HTMX's live
/// search never spins.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class UserSearchEndpointTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public UserSearchEndpointTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // The endpoint renders the matched person card (name + an "Add friend" affordance) for an exact
    // display-name term.
    [Fact]
    public async Task Exact_match_renders_person_card_with_add_friend_affordance()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Endpoint Seeker");
        using var target = await TestAuthentication.RegisterAndSignInAsync(_factory, "Endpoint Findable");

        using var response = await me.Client.GetAsync(
            $"/friends/search?term={Uri.EscapeDataString(target.DisplayName)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(target.DisplayName, html, StringComparison.Ordinal);
        Assert.Contains("Add friend", html, StringComparison.Ordinal);
    }

    // A term with no match (or, indistinguishably, a blocked user — §14) renders the calm empty state, still
    // as a 200.
    [Fact]
    public async Task No_match_renders_empty_state()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Endpoint Seeker Two");

        using var response = await me.Client.GetAsync(
            "/friends/search?term=someone-who-does-not-exist-xyz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("No user found", html, StringComparison.Ordinal);
    }
}
