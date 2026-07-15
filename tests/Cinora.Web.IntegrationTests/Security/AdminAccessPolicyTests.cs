using System.Security.Claims;
using Cinora.Web.Security;

namespace Cinora.Web.IntegrationTests.Security;

/// <summary>
/// Milestone 5.3 unit tests for <see cref="AdminAccessPolicy"/> — the pure decision behind the admin-gated
/// Hangfire <c>/jobs</c> dashboard, and the GUARANTEED dashboard-security evidence (no Hangfire storage or host
/// required). They pin the fail-closed contract: anonymous/null principals are denied; a non-admin authenticated
/// user is denied in Production; an allow-listed <b>server-assigned user-id</b> OR <b>email</b> is admitted
/// case-insensitively; a match on ONLY a free-form name claim is denied (proving the L1/L2 hardening removed the
/// name arm); the "any authenticated user" flag admits only in Development and never loosens Production; and the
/// out-of-the-box no-admins config denies everyone. Deliberately NOT in the factory collection — these are
/// host-free and parallel-safe.
/// </summary>
public sealed class AdminAccessPolicyTests
{
    private const string AdminEmail = "admin@cinora.test";

    // A server-assigned user-id GUID (ClaimTypes.NameIdentifier) on the allow-list — the unforgeable admit form.
    private static readonly Guid AdminUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly IReadOnlySet<string> NoAdmins =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> Admins =
        new HashSet<string>([AdminEmail, AdminUserId.ToString()], StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Null_principal_is_denied_even_with_the_development_flag()
    {
        var authorized = AdminAccessPolicy.IsAuthorized(
            user: null, isDevelopment: true, Admins, allowAnyAuthenticatedInDevelopment: true);

        Assert.False(authorized);
    }

    [Fact]
    public void Anonymous_principal_is_denied_even_with_the_development_flag()
    {
        // A ClaimsIdentity with no authentication type reports IsAuthenticated == false.
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var authorized = AdminAccessPolicy.IsAuthorized(
            anonymous, isDevelopment: true, Admins, allowAnyAuthenticatedInDevelopment: true);

        Assert.False(authorized);
    }

    [Fact]
    public void Authenticated_non_admin_is_denied_in_production()
    {
        var user = Authenticated(userId: Guid.NewGuid().ToString(), email: "someone@cinora.test");

        var authorized = AdminAccessPolicy.IsAuthorized(
            user, isDevelopment: false, Admins, allowAnyAuthenticatedInDevelopment: false);

        Assert.False(authorized);
    }

    [Fact]
    public void Admin_email_match_is_authorized_case_insensitively()
    {
        var user = Authenticated(userId: Guid.NewGuid().ToString(), email: "ADMIN@Cinora.TEST");

        var authorized = AdminAccessPolicy.IsAuthorized(
            user, isDevelopment: false, Admins, allowAnyAuthenticatedInDevelopment: false);

        Assert.True(authorized);
    }

    [Fact]
    public void Admin_user_id_match_is_authorized_case_insensitively()
    {
        // The server-assigned user-id (NameIdentifier) is the unforgeable admit form; matched case-insensitively.
        var user = Authenticated(userId: AdminUserId.ToString().ToUpperInvariant(), email: "not-admin@cinora.test");

        var authorized = AdminAccessPolicy.IsAuthorized(
            user, isDevelopment: false, Admins, allowAnyAuthenticatedInDevelopment: false);

        Assert.True(authorized);
    }

    [Fact]
    public void Match_on_only_a_free_form_name_claim_is_denied()
    {
        // L1/L2 hardening: the name arm is gone. This principal's ONLY allow-listed value is its free-form name
        // claim (equal to an admin email); its server-assigned user-id and email do NOT match. It must be denied,
        // proving authZ no longer keys off Identity.Name / ClaimTypes.Name.
        var user = Authenticated(userId: Guid.NewGuid().ToString(), email: "not-admin@cinora.test", name: AdminEmail);

        var authorized = AdminAccessPolicy.IsAuthorized(
            user, isDevelopment: false, Admins, allowAnyAuthenticatedInDevelopment: false);

        Assert.False(authorized);
    }

    [Fact]
    public void Any_authenticated_user_is_authorized_in_development_when_the_flag_is_set()
    {
        var user = Authenticated(email: "someone@cinora.test");

        var authorized = AdminAccessPolicy.IsAuthorized(
            user, isDevelopment: true, NoAdmins, allowAnyAuthenticatedInDevelopment: true);

        Assert.True(authorized);
    }

    [Fact]
    public void The_development_flag_does_not_loosen_production()
    {
        var user = Authenticated(email: "someone@cinora.test");

        var authorized = AdminAccessPolicy.IsAuthorized(
            user, isDevelopment: false, NoAdmins, allowAnyAuthenticatedInDevelopment: true);

        Assert.False(authorized);
    }

    [Fact]
    public void No_admins_configured_denies_everyone_by_default_fail_closed()
    {
        var user = Authenticated(userId: Guid.NewGuid().ToString(), email: "someone@cinora.test");

        var authorized = AdminAccessPolicy.IsAuthorized(
            user, isDevelopment: false, NoAdmins, allowAnyAuthenticatedInDevelopment: false);

        Assert.False(authorized);
    }

    // A principal authenticated with an explicit authentication type (so Identity.IsAuthenticated == true),
    // carrying whichever of the server-assigned user-id (NameIdentifier), email, and free-form name claims the
    // caller supplies. Only user-id + email are matched by the policy; name is included only to prove it is NOT.
    private static ClaimsPrincipal Authenticated(string? userId = null, string? email = null, string? name = null)
    {
        var claims = new List<Claim>();

        if (userId is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
        }

        if (email is not null)
        {
            claims.Add(new Claim(ClaimTypes.Email, email));
        }

        if (name is not null)
        {
            claims.Add(new Claim(ClaimTypes.Name, name));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestCookie"));
    }
}
