using System.Net;
using Cinora.Web.IntegrationTests.Infrastructure;

namespace Cinora.Web.IntegrationTests.Friends;

/// <summary>
/// Tasks B3.2 + B3.3 tests for the redesigned social hub and the repointed empty-state CTA: the <c>/friends</c>
/// hub renders the Find-people search box and the Blocked section (with a blocked user's card), and the
/// authenticated <c>/home</c> no-friends empty state points at the Friends hub ("Find friends"), NOT the movie
/// Discovery page.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class FriendsHubTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public FriendsHubTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Hub_shows_find_people_search_and_blocked_section()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Hub Owner");
        using var blocked = await TestAuthentication.RegisterAndSignInAsync(_factory, "Hub Blocked Person");

        await BlockAsync(me.Client, blocked.UserId);

        using var page = await me.Client.GetAsync("/friends");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();

        Assert.Contains("/friends/search", html, StringComparison.Ordinal);   // the Find-people search box
        Assert.Contains("Blocked", html, StringComparison.Ordinal);           // the Blocked section
        Assert.Contains(blocked.DisplayName, html, StringComparison.Ordinal); // the blocked user's card
    }

    [Fact]
    public async Task Home_no_friends_empty_state_points_at_find_friends_not_discovery()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Lonely Lee");

        using var page = await me.Client.GetAsync("/home");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();

        Assert.Contains("data-feed-empty=\"no-friends\"", html, StringComparison.Ordinal);
        Assert.Contains(">Find friends</a>", html, StringComparison.Ordinal); // the primary CTA button
        Assert.DoesNotContain("Discover titles", html, StringComparison.Ordinal); // the old CTA is gone
    }

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
