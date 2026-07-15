using System.Net;
using Cinora.Domain.Entities;
using Cinora.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Web.IntegrationTests.Infrastructure;

/// <summary>
/// An authenticated integration-test user: the <see cref="HttpClient"/> whose cookie container holds the
/// Identity auth cookie issued by registration (so subsequent requests act as this user), plus the server-side
/// <see cref="UserId"/> the app resolves from the auth cookie's <c>NameIdentifier</c> claim (the same id
/// <c>ICurrentUser</c> returns). Handed out by <see cref="TestAuthentication.RegisterAndSignInAsync"/>.
/// </summary>
/// <param name="Client">The authenticated HTTP client (auth cookie carried automatically).</param>
/// <param name="UserId">The user's id — the shared <c>ApplicationUser</c>/domain-<c>User</c> primary key.</param>
/// <param name="Email">The unique email the user registered with.</param>
/// <param name="DisplayName">The display name the user registered with.</param>
internal sealed record AuthenticatedTestUser(HttpClient Client, Guid UserId, string Email, string DisplayName)
    : IDisposable
{
    public void Dispose() => Client.Dispose();
}

/// <summary>
/// Establishes authenticated integration-test clients by driving the REAL registration/login flow (there is no
/// fake sign-in shortcut — this exercises the same MVC + Identity + anti-forgery pipeline production uses, and
/// the resulting cookie is what <c>ICurrentUser</c> resolves the acting user from). Reusable by every
/// authenticated Phase-3 suite (reviews, likes/comments, friends, notifications).
/// </summary>
internal static class TestAuthentication
{
    // Satisfies the Identity password policy configured in Program.cs (>= 8, upper, lower, digit, symbol).
    private const string Password = "Test1234!";

    /// <summary>
    /// Registers a brand-new user through <c>POST /account/register</c> (which signs them in and sets the auth
    /// cookie on the returned client) and resolves their server-side id. Each call uses a unique email, so
    /// callers can register as many independent users as a test needs.
    /// </summary>
    /// <param name="factory">The (fake-TMDB-backed) factory whose server + shared database to use.</param>
    /// <param name="displayName">The display name to register with.</param>
    /// <returns>The authenticated client bundled with the resolved user id.</returns>
    public static async Task<AuthenticatedTestUser> RegisterAndSignInAsync(
        WebApplicationFactory<Program> factory, string displayName)
    {
        var client = CreateClient(factory);
        var email = $"reviewer-{Guid.NewGuid():N}@cinora.test";

        // Display names are now globally UNIQUE (case-insensitive), so make every registered test user's name
        // unique while keeping the caller's requested name as a prefix (so `Contains(displayName)` assertions
        // still pass). The actual unique name is returned on AuthenticatedTestUser for exact-match assertions.
        var uniqueDisplayName = UniquifyDisplayName(displayName);

        var token = await AntiforgeryTokenAsync(client, "/account/register");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["DisplayName"] = uniqueDisplayName,
            ["Email"] = email,
            ["Password"] = Password,
            ["ConfirmPassword"] = Password,
            ["__RequestVerificationToken"] = token,
        });

        using var response = await client.PostAsync("/account/register", content);
        if (response.StatusCode != HttpStatusCode.Redirect)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Test registration for '{email}' did not sign in (expected 302, got {(int)response.StatusCode}). Body: {body}");
        }

        Guid userId;
        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var applicationUser = await userManager.FindByEmailAsync(email)
                ?? throw new InvalidOperationException($"Registered user '{email}' was not found.");
            userId = applicationUser.Id;
        }

        return new AuthenticatedTestUser(client, userId, email, uniqueDisplayName);
    }

    /// <summary>
    /// Fetches a valid anti-forgery request token from a rendered page (the anti-forgery cookie flows back on
    /// the same client automatically). Use the returned token as the <c>__RequestVerificationToken</c> form
    /// field OR the <c>RequestVerificationToken</c> header (both are accepted by the token store). An
    /// authenticated client can read it from any page (e.g. the layout logout form).
    /// </summary>
    /// <param name="client">The client whose cookie container should receive the anti-forgery cookie.</param>
    /// <param name="path">A page whose rendered HTML contains the anti-forgery hidden field.</param>
    /// <returns>The request token to send back.</returns>
    public static async Task<string> AntiforgeryTokenAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        return AntiForgeryTokenExtractor.Extract(html);
    }

    /// <summary>Creates a non-redirect-following client against the <c>https://localhost</c> base (Secure cookie).</summary>
    /// <param name="factory">The factory to create the client from.</param>
    /// <returns>A configured <see cref="HttpClient"/>.</returns>
    public static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    // Appends a unique token so the globally-unique display-name constraint is never violated by two test users
    // sharing a base name; the requested name stays a prefix so substring assertions on it still hold.
    private static string UniquifyDisplayName(string displayName)
    {
        var candidate = $"{displayName} {Guid.NewGuid():N}";
        return candidate.Length <= User.DisplayNameMaxLength ? candidate : candidate[..User.DisplayNameMaxLength];
    }
}
