using System.Net;
using Cinora.Application.Common.Interfaces;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Web.IntegrationTests.Notifications;

/// <summary>
/// Milestone 6.3 (ADR 0020 §5.3) integration tests for the owner-only <c>/settings/notifications</c> surface,
/// driven end-to-end through the real MVC + hand-rolled <c>ISender</c> + Identity pipeline against the migrated
/// <c>CinoraTest</c> LocalDB (so the additive Phase-6 migration is proven to apply cleanly). They prove the
/// fail-closed + anti-forgery contract (anon → login redirect; authed-without-token → 400), that a POST updates
/// the CURRENT user's per-type push preferences (queried back from the row), that the page renders the toggles +
/// the subscribe control, and that the surface does not widen the strict CSP.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class NotificationSettingsTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public NotificationSettingsTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Get_anonymous_is_redirected_to_login()
    {
        using var anon = TestAuthentication.CreateClient(_factory);

        using var response = await anon.GetAsync("/settings/notifications");

        AssertLoginRedirect(response);
    }

    [Fact]
    public async Task Post_anonymous_is_redirected_to_login()
    {
        using var anon = TestAuthentication.CreateClient(_factory);

        using var response = await anon.PostAsync(
            "/settings/notifications",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["PushFriendRequests"] = "true",
                ["PushFriendAccepted"] = "true",
                ["PushReviewLikes"] = "true",
                ["PushComments"] = "true",
            }));

        AssertLoginRedirect(response);
    }

    [Fact]
    public async Task Post_authenticated_without_antiforgery_token_is_rejected()
    {
        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "Prefs User");

        using var response = await PostPreferencesAsync(
            user.Client, token: null, friendRequests: false, friendAccepted: true, reviewLikes: false, comments: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_with_token_updates_the_current_users_preferences()
    {
        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "Prefs User");
        var token = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/settings/notifications");

        using var response = await PostPreferencesAsync(
            user.Client, token, friendRequests: false, friendAccepted: true, reviewLikes: false, comments: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var prefs = await PreferencesForAsync(user.UserId);
        Assert.False(prefs.PushFriendRequests);
        Assert.True(prefs.PushFriendAccepted);
        Assert.False(prefs.PushReviewLikes);
        Assert.True(prefs.PushComments);
    }

    [Fact]
    public async Task Get_renders_the_toggles_and_the_subscribe_control()
    {
        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "Prefs User");

        using var response = await user.Client.GetAsync("/settings/notifications");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Likes on your reviews", html, StringComparison.Ordinal);
        Assert.Contains("Comments on your reviews", html, StringComparison.Ordinal);
        Assert.Contains("Friend requests", html, StringComparison.Ordinal);
        Assert.Contains("data-push-enable", html, StringComparison.Ordinal); // the subscribe control moved here
        Assert.Contains("notification-prefs-form", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Notification_settings_page_carries_the_unchanged_strict_csp()
    {
        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "CSP Prefs User");

        using var response = await user.Client.GetAsync("/settings/notifications");

        Assert.True(
            response.Headers.TryGetValues("Content-Security-Policy", out var values),
            "The /settings/notifications page must carry the strict CSP header (SecurityHeadersMiddleware unchanged).");
        var csp = Assert.Single(values!);

        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> PostPreferencesAsync(
        HttpClient client, string? token, bool friendRequests, bool friendAccepted, bool reviewLikes, bool comments)
    {
        var fields = new Dictionary<string, string>
        {
            ["PushFriendRequests"] = friendRequests ? "true" : "false",
            ["PushFriendAccepted"] = friendAccepted ? "true" : "false",
            ["PushReviewLikes"] = reviewLikes ? "true" : "false",
            ["PushComments"] = comments ? "true" : "false",
        };
        if (token is not null)
        {
            fields["__RequestVerificationToken"] = token;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/settings/notifications")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        request.Headers.Add("HX-Request", "true"); // ask for the fragment (200) rather than a redirect
        return await client.SendAsync(request);
    }

    private async Task<(bool PushFriendRequests, bool PushFriendAccepted, bool PushReviewLikes, bool PushComments)>
        PreferencesForAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        // IAppDbContext.Users is the DOMAIN User set (carries the owned NotificationPreferences columns).
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var row = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.Preferences.PushFriendRequests,
                u.Preferences.PushFriendAccepted,
                u.Preferences.PushReviewLikes,
                u.Preferences.PushComments,
            })
            .SingleAsync();
        return (row.PushFriendRequests, row.PushFriendAccepted, row.PushReviewLikes, row.PushComments);
    }

    private static void AssertLoginRedirect(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.OriginalString ?? string.Empty;
        Assert.Contains("/account/login", location, StringComparison.OrdinalIgnoreCase);
    }
}
