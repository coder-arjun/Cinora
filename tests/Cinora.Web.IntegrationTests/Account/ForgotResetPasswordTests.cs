using System.Net;
using System.Text.RegularExpressions;
using Cinora.Application.Common.Interfaces;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cinora.Web.IntegrationTests.Account;

/// <summary>
/// End-to-end coverage for the forgot/reset-password flow over the real MVC + Identity + anti-forgery pipeline
/// against the migrated <c>CinoraTest</c> LocalDB. The SMTP adapter is replaced by a <see cref="FakeEmailSender"/>
/// so the reset link can be read out of the captured email body — no mail server. Verifies: the neutral,
/// no-enumeration response for unknown addresses; that a real account gets a working link; that the link resets
/// the password (new password signs in, old one no longer does); and that a malformed link is rejected.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed partial class ForgotResetPasswordTests : IAsyncLifetime
{
    private const string NewPassword = "NewPass9!x";

    private readonly CinoraWebApplicationFactory _factory;

    public ForgotResetPasswordTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ForgotPassword_get_renders_the_form()
    {
        using var client = TestAuthentication.CreateClient(_factory);

        using var response = await client.GetAsync("/account/forgot-password");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Forgot your password?", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgotPassword_unknown_email_redirects_to_confirmation_and_sends_nothing()
    {
        var fakeEmail = new FakeEmailSender();
        await using var factory = WithFakeEmail(fakeEmail);
        using var client = TestAuthentication.CreateClient(factory);

        using var response = await PostForgotPasswordAsync(client, $"nobody-{Guid.NewGuid():N}@cinora.test");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/forgot-password-confirmation", response.Headers.Location?.OriginalString);
        Assert.Empty(fakeEmail.Sent); // no account matched → no email, and no leak that it didn't match
    }

    [Fact]
    public async Task ForgotPassword_known_email_emails_a_link_that_resets_the_password()
    {
        var fakeEmail = new FakeEmailSender();
        await using var factory = WithFakeEmail(fakeEmail);

        // A real account (registration also signs in, but the reset flow runs on fresh anonymous clients).
        using var registered = await TestAuthentication.RegisterAndSignInAsync(factory, "Pat Reset");
        var email = registered.Email;

        // 1) Request the reset link.
        using (var requester = TestAuthentication.CreateClient(factory))
        using (var forgot = await PostForgotPasswordAsync(requester, email))
        {
            Assert.Equal(HttpStatusCode.Redirect, forgot.StatusCode);
        }

        Assert.NotNull(fakeEmail.Last);
        Assert.Equal(email, fakeEmail.Last!.To);
        Assert.Contains("Reset your Cinora password", fakeEmail.Last.Subject, StringComparison.Ordinal);

        var resetUrl = ExtractResetUrl(fakeEmail.Last.HtmlBody);
        var query = QueryHelpers.ParseQuery(new Uri(resetUrl).Query);
        var linkEmail = query["email"].ToString();
        var code = query["code"].ToString();
        Assert.Equal(email, linkEmail);
        Assert.NotEmpty(code);

        // 2) Open the reset form (carries the anti-forgery cookie) and post the new password.
        using var resetClient = TestAuthentication.CreateClient(factory);
        var resetToken = await TestAuthentication.AntiforgeryTokenAsync(resetClient, new Uri(resetUrl).PathAndQuery);
        using (var reset = await PostFormAsync(resetClient, "/account/reset-password", new Dictionary<string, string>
        {
            ["Email"] = linkEmail,
            ["Code"] = code,
            ["Password"] = NewPassword,
            ["ConfirmPassword"] = NewPassword,
            ["__RequestVerificationToken"] = resetToken,
        }))
        {
            Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
            Assert.Equal("/account/reset-password-confirmation", reset.Headers.Location?.OriginalString);
        }

        // 3) The NEW password signs in (302); the OLD one no longer does (200 with error).
        Assert.Equal(HttpStatusCode.Redirect, await SignInStatusAsync(factory, email, NewPassword));
        Assert.Equal(HttpStatusCode.OK, await SignInStatusAsync(factory, email, "Test1234!"));
    }

    [Fact]
    public async Task ResetPassword_get_without_a_code_redirects_to_login()
    {
        using var client = TestAuthentication.CreateClient(_factory);

        using var response = await client.GetAsync("/account/reset-password");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/account/login", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private WebApplicationFactory<Program> WithFakeEmail(FakeEmailSender fakeEmail) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(fakeEmail);
            }));

    private static async Task<HttpResponseMessage> PostForgotPasswordAsync(HttpClient client, string email)
    {
        var token = await TestAuthentication.AntiforgeryTokenAsync(client, "/account/forgot-password");
        return await PostFormAsync(client, "/account/forgot-password", new Dictionary<string, string>
        {
            ["Email"] = email,
            ["__RequestVerificationToken"] = token,
        });
    }

    // Drives POST /account/login the way the sign-in form does; returns the status (302 = success, 200 = error).
    private static async Task<HttpStatusCode> SignInStatusAsync(
        WebApplicationFactory<Program> factory, string email, string password)
    {
        using var client = TestAuthentication.CreateClient(factory);
        var token = await TestAuthentication.AntiforgeryTokenAsync(client, "/account/login");
        using var response = await PostFormAsync(client, "/account/login", new Dictionary<string, string>
        {
            ["EmailOrUserName"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] = token,
        });
        return response.StatusCode;
    }

    private static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string path, Dictionary<string, string> fields)
    {
        using var content = new FormUrlEncodedContent(fields);
        return await client.PostAsync(path, content);
    }

    private static string ExtractResetUrl(string html)
    {
        var match = ResetHrefRegex().Match(html);
        Assert.True(match.Success, "The reset email did not contain a reset-password link.");
        return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [GeneratedRegex("href=\"([^\"]*reset-password[^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ResetHrefRegex();
}
