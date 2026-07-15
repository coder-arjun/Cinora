using System.Net;
using Cinora.Domain.Entities;
using Cinora.Domain.Exceptions;
using Cinora.Infrastructure.Identity;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Web.IntegrationTests.Authentication;

/// <summary>
/// Exercises the Milestone 1.3 authentication vertical end-to-end through the real MVC + Identity
/// pipeline against a migrated LocalDB database — public/anonymous routing, the anti-forgery-protected
/// register and login flows, and (the 1.3 blocking gate) atomic-registration orphan prevention.
/// </summary>
/// <remarks>
/// All tests share one <see cref="CinoraWebApplicationFactory"/> and run sequentially (xUnit never
/// parallelizes within a class), so the shared <c>CinoraTest</c> database is migrated once and each test
/// uses a unique email — making the suite deterministic and order-independent without per-test resets.
/// The application cookie is <c>Secure</c>-only, so every client uses an <c>https://localhost</c> base
/// address (TestServer honours the scheme without real TLS); <see cref="WebApplicationFactoryClientOptions.AllowAutoRedirect"/>
/// is off so 302s can be asserted directly.
/// </remarks>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class AuthenticationFlowTests : IAsyncLifetime
{
    private const string LandingPath = "/";
    private const string LoginPath = "/account/login";
    private const string RegisterPath = "/account/register";
    private const string HomePath = "/home";
    // Post-authentication landing: registration and login now redirect to Discover (the browse hub), not /home.
    private const string DiscoverPath = "/discover";

    // Satisfies the Identity policy: length >= 8, upper, lower, digit, and non-alphanumeric.
    private const string ValidPassword = "Test1234!";

    private readonly CinoraWebApplicationFactory _factory;

    public AuthenticationFlowTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Get_landing_page_anonymously_returns_ok()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync(LandingPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // A signed-in user must NEVER see the marketing landing ("Get started / Sign in") — the root redirects them
    // to the Discover browse instead.
    [Fact]
    public async Task Get_landing_when_authenticated_redirects_to_discover()
    {
        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "Landing Redirect");

        using var response = await user.Client.GetAsync(LandingPath);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/discover", response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_login_page_returns_ok()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync(LoginPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_register_page_returns_ok()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync(RegisterPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_home_anonymously_redirects_to_login_with_return_url()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync(HomePath);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.OriginalString;
        Assert.NotNull(location);
        Assert.Contains("/account/login", location, StringComparison.Ordinal);
        Assert.Contains("ReturnUrl=%2Fhome", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_with_valid_data_creates_shared_identity_and_domain_user_and_authenticates()
    {
        using var client = CreateClient();
        var email = UniqueEmail();
        var displayName = UniqueDisplayName("Ada Lovelace");

        using var response = await SubmitFormAsync(client, RegisterPath, new Dictionary<string, string>
        {
            ["DisplayName"] = displayName,
            ["Email"] = email,
            ["Password"] = ValidPassword,
            ["ConfirmPassword"] = ValidPassword,
        });

        // PRG: a successful registration signs in and redirects to the protected Discover hub.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.EndsWith(DiscoverPath, response.Headers.Location?.OriginalString);

        // Both rows were committed together and share the SAME Guid primary key (ADR 0003).
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var dbContext = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

            var applicationUser = await userManager.FindByEmailAsync(email);
            Assert.NotNull(applicationUser);

            var domainUser = await dbContext.Set<User>()
                .SingleOrDefaultAsync(u => u.Id == applicationUser!.Id, CancellationToken.None);
            Assert.NotNull(domainUser);
            Assert.Equal(applicationUser!.Id, domainUser!.Id);
            Assert.Equal(displayName, domainUser.DisplayName);
        }

        // The registration cookie authenticates the same client against [Authorize] home.
        using var home = await client.GetAsync(HomePath);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
    }

    [Fact]
    public async Task Login_with_valid_credentials_authenticates_the_user()
    {
        var email = UniqueEmail();
        await RegisterUserAsync(email, "Grace Hopper");

        // A fresh, unauthenticated session signs in with the credentials just created.
        using var client = CreateClient();
        using var loginResponse = await SubmitFormAsync(client, LoginPath, new Dictionary<string, string>
        {
            ["EmailOrUserName"] = email,
            ["Password"] = ValidPassword,
            ["RememberMe"] = "false",
        });

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

        using var home = await client.GetAsync(HomePath);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
    }

    [Fact]
    public async Task Login_with_wrong_password_is_rejected_with_a_generic_error()
    {
        var email = UniqueEmail();
        await RegisterUserAsync(email, "Alan Turing");

        using var client = CreateClient();
        using var loginResponse = await SubmitFormAsync(client, LoginPath, new Dictionary<string, string>
        {
            ["EmailOrUserName"] = email,
            ["Password"] = "Wrong-Password-9!",
            ["RememberMe"] = "false",
        });

        // A failed sign-in redisplays the form (200) with a message that never reveals whether the
        // email exists (no user enumeration).
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var body = await loginResponse.Content.ReadAsStringAsync();
        Assert.Contains("Invalid login attempt.", body, StringComparison.Ordinal);

        // No auth cookie was issued, so the protected page still challenges.
        using var home = await client.GetAsync(HomePath);
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
    }

    [Fact]
    public async Task Login_with_display_name_instead_of_email_authenticates_the_user()
    {
        var email = UniqueEmail();
        var displayName = await RegisterUserAsync(email, "Handle User");

        // Sign in with the DISPLAY NAME (upper-cased to prove the match is case-insensitive), not the email.
        using var client = CreateClient();
        using var loginResponse = await SubmitFormAsync(client, LoginPath, new Dictionary<string, string>
        {
            ["EmailOrUserName"] = displayName.ToUpperInvariant(),
            ["Password"] = ValidPassword,
            ["RememberMe"] = "false",
        });

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

        using var home = await client.GetAsync(HomePath);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
    }

    [Fact]
    public async Task RegisterAsync_with_a_duplicate_display_name_is_rejected_and_writes_no_new_rows()
    {
        var firstDisplayName = await RegisterUserAsync(UniqueEmail(), "Twin User");
        var secondEmail = UniqueEmail();

        int domainUsersBefore;
        UserRegistrationResult duplicateResult;
        using (var scope = _factory.Services.CreateScope())
        {
            var registrationService = scope.ServiceProvider.GetRequiredService<IUserRegistrationService>();
            var dbContext = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
            domainUsersBefore = await dbContext.Set<User>().CountAsync(CancellationToken.None);

            // Same display name in a different case → rejected up front (case-insensitive unique handle),
            // rolling back the AspNetUsers row so neither a new identity nor a domain row is written.
            duplicateResult = await registrationService.RegisterAsync(
                secondEmail, ValidPassword, firstDisplayName.ToLowerInvariant(), CancellationToken.None);
        }

        Assert.False(duplicateResult.Succeeded);
        Assert.Contains(duplicateResult.Errors, e => e.Contains("display name", StringComparison.OrdinalIgnoreCase));

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var dbContext = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

            Assert.Null(await userManager.FindByEmailAsync(secondEmail));
            Assert.Equal(domainUsersBefore, await dbContext.Set<User>().CountAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task Login_page_shows_no_external_providers_when_google_is_not_configured()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync(LoginPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Or sign in with", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// BLOCKING (Milestone 1.3): a valid email/password with an over-length display name makes the
    /// Identity principal create successfully and then the domain <see cref="User"/> factory throw — the
    /// atomic <see cref="IUserRegistrationService"/> MUST roll the whole transaction back, leaving no
    /// orphan <c>AspNetUsers</c> row and no domain <c>Users</c> row.
    /// </summary>
    [Fact]
    public async Task RegisterAsync_with_invalid_display_name_rolls_back_and_leaves_no_orphan_identity_row()
    {
        var email = UniqueEmail();
        var invalidDisplayName = new string('x', User.DisplayNameMaxLength + 1);

        int domainUsersBefore;
        using (var scope = _factory.Services.CreateScope())
        {
            var registrationService = scope.ServiceProvider.GetRequiredService<IUserRegistrationService>();
            var dbContext = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
            domainUsersBefore = await dbContext.Set<User>().CountAsync(CancellationToken.None);

            // Valid credentials → UserManager.CreateAsync inserts AspNetUsers inside the transaction;
            // the over-length name then throws from User.Create, forcing the rollback + rethrow.
            await Assert.ThrowsAsync<DomainException>(() =>
                registrationService.RegisterAsync(email, ValidPassword, invalidDisplayName, CancellationToken.None));
        }

        // Fresh scope: the rollback must have removed the AspNetUsers row and added no domain row.
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var dbContext = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

            Assert.Null(await userManager.FindByEmailAsync(email));
            Assert.Equal(domainUsersBefore, await dbContext.Set<User>().CountAsync(CancellationToken.None));
        }
    }

    /// <summary>
    /// CR1: the CreateAsync-failure branch (complements the domain-throw orphan test). Re-registering an
    /// already-used email with otherwise valid credentials must make <c>UserManager.CreateAsync</c> fail
    /// the unique-email rule; the atomic service returns a Failure and rolls back cleanly, so no SECOND
    /// <c>AspNetUsers</c> row and no extra domain <c>Users</c> row are written.
    /// </summary>
    [Fact]
    public async Task RegisterAsync_with_a_duplicate_email_fails_and_leaves_a_single_identity_row()
    {
        var email = UniqueEmail();
        await RegisterUserAsync(email, "Katherine Johnson");

        int domainUsersBefore;
        UserRegistrationResult duplicateResult;
        using (var scope = _factory.Services.CreateScope())
        {
            var registrationService = scope.ServiceProvider.GetRequiredService<IUserRegistrationService>();
            var dbContext = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
            domainUsersBefore = await dbContext.Set<User>().CountAsync(CancellationToken.None);

            // Valid password + display name, but the email already exists → CreateAsync fails the
            // unique-email rule before any domain row is added, forcing the non-cancellable rollback.
            duplicateResult = await registrationService.RegisterAsync(
                email, ValidPassword, "Katherine Impersonator", CancellationToken.None);
        }

        // The duplicate is reported as a failure (never a silent success) and carries no committed id.
        Assert.False(duplicateResult.Succeeded);
        Assert.Equal(Guid.Empty, duplicateResult.UserId);
        Assert.NotEmpty(duplicateResult.Errors);

        // Exactly one AspNetUsers row still owns the email, and the domain Users table is unchanged.
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

            Assert.Equal(1, await dbContext.Users.CountAsync(u => u.Email == email, CancellationToken.None));
            Assert.Equal(domainUsersBefore, await dbContext.Set<User>().CountAsync(CancellationToken.None));
        }
    }

    /// <summary>
    /// CR2 (happy path): external registration with valid inputs commits the Identity principal and the
    /// shared-PK domain <see cref="User"/> together, links the external login, and confirms the email
    /// only when the provider asserted it (<paramref name="emailVerified"/>) — fail-closed otherwise (F5).
    /// </summary>
    [Theory]
    [InlineData(true)]  // provider asserted the email is verified → EmailConfirmed
    [InlineData(false)] // provider did not assert verification → stays unconfirmed
    public async Task RegisterExternalAsync_with_valid_inputs_creates_shared_user_and_confirms_email_per_flag(
        bool emailVerified)
    {
        var email = UniqueEmail();
        var providerKey = Guid.NewGuid().ToString("N");
        const string provider = "Google";
        var displayName = UniqueDisplayName("External Ada");

        UserRegistrationResult result;
        using (var scope = _factory.Services.CreateScope())
        {
            var registrationService = scope.ServiceProvider.GetRequiredService<IUserRegistrationService>();
            result = await registrationService.RegisterExternalAsync(
                provider, providerKey, email, displayName, emailVerified, CancellationToken.None);
        }

        Assert.True(result.Succeeded);
        Assert.NotEqual(Guid.Empty, result.UserId);

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var dbContext = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

            var applicationUser = await userManager.FindByEmailAsync(email);
            Assert.NotNull(applicationUser);
            Assert.Equal(result.UserId, applicationUser!.Id);

            // F5: EmailConfirmed mirrors the provider assertion exactly.
            Assert.Equal(emailVerified, applicationUser.EmailConfirmed);

            // The external login was linked (proves the AddLoginAsync step ran and committed).
            var linkedUser = await userManager.FindByLoginAsync(provider, providerKey);
            Assert.NotNull(linkedUser);
            Assert.Equal(applicationUser.Id, linkedUser!.Id);

            // Both rows share the SAME Guid primary key (ADR 0003).
            var domainUser = await dbContext.Set<User>()
                .SingleOrDefaultAsync(u => u.Id == applicationUser.Id, CancellationToken.None);
            Assert.NotNull(domainUser);
            Assert.Equal(applicationUser.Id, domainUser!.Id);
            Assert.Equal(displayName, domainUser.DisplayName);
        }
    }

    /// <summary>
    /// CR2 (domain-throw rollback): a valid external email with an over-length display name lets
    /// <c>CreateAsync</c> and <c>AddLoginAsync</c> succeed inside the transaction, then makes
    /// <see cref="User.Create"/> throw — the service MUST roll everything back, leaving no orphan
    /// <c>AspNetUsers</c> row and no domain <c>Users</c> row.
    /// </summary>
    [Fact]
    public async Task RegisterExternalAsync_with_invalid_display_name_rolls_back_and_leaves_no_orphan_row()
    {
        var email = UniqueEmail();
        var providerKey = Guid.NewGuid().ToString("N");
        var invalidDisplayName = new string('x', User.DisplayNameMaxLength + 1);

        int domainUsersBefore;
        using (var scope = _factory.Services.CreateScope())
        {
            var registrationService = scope.ServiceProvider.GetRequiredService<IUserRegistrationService>();
            var dbContext = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
            domainUsersBefore = await dbContext.Set<User>().CountAsync(CancellationToken.None);

            await Assert.ThrowsAsync<DomainException>(() =>
                registrationService.RegisterExternalAsync(
                    "Google", providerKey, email, invalidDisplayName, emailVerified: true, CancellationToken.None));
        }

        // Fresh scope: the rollback removed the AspNetUsers row and added no domain row.
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var dbContext = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

            Assert.Null(await userManager.FindByEmailAsync(email));
            Assert.Equal(domainUsersBefore, await dbContext.Set<User>().CountAsync(CancellationToken.None));
        }
    }

    /// <summary>
    /// CR2 (duplicate path): an email already registered locally makes external <c>CreateAsync</c> fail
    /// the unique-email rule (Cinora never auto-links — F5), so the service returns a Failure and writes
    /// no second <c>AspNetUsers</c> row and no extra domain <c>Users</c> row.
    /// </summary>
    [Fact]
    public async Task RegisterExternalAsync_with_an_already_registered_email_fails_and_leaves_a_single_identity_row()
    {
        var email = UniqueEmail();
        await RegisterUserAsync(email, "Grace Hopper");

        int domainUsersBefore;
        UserRegistrationResult result;
        using (var scope = _factory.Services.CreateScope())
        {
            var registrationService = scope.ServiceProvider.GetRequiredService<IUserRegistrationService>();
            var dbContext = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
            domainUsersBefore = await dbContext.Set<User>().CountAsync(CancellationToken.None);

            result = await registrationService.RegisterExternalAsync(
                "Google", Guid.NewGuid().ToString("N"), email, "Grace Externally", emailVerified: true,
                CancellationToken.None);
        }

        Assert.False(result.Succeeded);
        Assert.Equal(Guid.Empty, result.UserId);
        Assert.NotEmpty(result.Errors);

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

            Assert.Equal(1, await dbContext.Users.CountAsync(u => u.Email == email, CancellationToken.None));
            Assert.Equal(domainUsersBefore, await dbContext.Set<User>().CountAsync(CancellationToken.None));
        }
    }

    /// <summary>
    /// CR5: the open-redirect guard (<c>Url.IsLocalUrl</c> in <c>RedirectToLocal</c>). A successful login
    /// whose <c>ReturnUrl</c> points at an external site must NOT redirect there — it falls back to the
    /// authenticated default landing (Discover).
    /// </summary>
    [Fact]
    public async Task Login_with_an_external_return_url_redirects_to_local_default_not_the_external_site()
    {
        var email = UniqueEmail();
        await RegisterUserAsync(email, "Margaret Hamilton");

        using var client = CreateClient();
        using var response = await SubmitFormAsync(client, LoginPath, new Dictionary<string, string>
        {
            ["EmailOrUserName"] = email,
            ["Password"] = ValidPassword,
            ["RememberMe"] = "false",
            ["ReturnUrl"] = "https://evil.example.com/steal",
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.OriginalString;
        Assert.NotNull(location);
        Assert.DoesNotContain("evil.example.com", location, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DiscoverPath, location);
    }

    /// <summary>
    /// CR5: the flip side of the guard — a genuinely LOCAL <c>ReturnUrl</c> is honoured. Using the
    /// landing path (<c>/</c>) rather than <c>/home</c> keeps it distinguishable from the fallback, so
    /// the assertion proves the URL was honoured and not merely defaulted.
    /// </summary>
    [Fact]
    public async Task Login_with_a_local_return_url_honours_it()
    {
        var email = UniqueEmail();
        await RegisterUserAsync(email, "Radia Perlman");

        using var client = CreateClient();
        using var response = await SubmitFormAsync(client, LoginPath, new Dictionary<string, string>
        {
            ["EmailOrUserName"] = email,
            ["Password"] = ValidPassword,
            ["RememberMe"] = "false",
            ["ReturnUrl"] = LandingPath,
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(LandingPath, response.Headers.Location?.OriginalString);
    }

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    // Registers a user through the full HTTP pipeline and asserts the happy-path redirect. The display name is
    // made unique (the constraint is global) and RETURNED so a caller can then sign in by it.
    private async Task<string> RegisterUserAsync(string email, string displayName)
    {
        var uniqueDisplayName = UniqueDisplayName(displayName);
        using var client = CreateClient();
        using var response = await SubmitFormAsync(client, RegisterPath, new Dictionary<string, string>
        {
            ["DisplayName"] = uniqueDisplayName,
            ["Email"] = email,
            ["Password"] = ValidPassword,
            ["ConfirmPassword"] = ValidPassword,
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return uniqueDisplayName;
    }

    // Appends a unique token so the globally-unique display-name constraint is never violated across the shared
    // test database; the base name stays a prefix so any substring assertions still hold.
    private static string UniqueDisplayName(string baseName)
    {
        var candidate = $"{baseName} {Guid.NewGuid():N}";
        return candidate.Length <= User.DisplayNameMaxLength ? candidate : candidate[..User.DisplayNameMaxLength];
    }

    // GETs the form to obtain a valid anti-forgery token (cookie flows automatically via the client's
    // cookie container), then POSTs the fields plus the token.
    private static async Task<HttpResponseMessage> SubmitFormAsync(
        HttpClient client, string path, Dictionary<string, string> fields)
    {
        using var getResponse = await client.GetAsync(path);
        getResponse.EnsureSuccessStatusCode();
        var html = await getResponse.Content.ReadAsStringAsync();
        fields["__RequestVerificationToken"] = AntiForgeryTokenExtractor.Extract(html);

        using var content = new FormUrlEncodedContent(fields);
        return await client.PostAsync(path, content);
    }

    private static string UniqueEmail() => $"user-{Guid.NewGuid():N}@cinora.test";
}
