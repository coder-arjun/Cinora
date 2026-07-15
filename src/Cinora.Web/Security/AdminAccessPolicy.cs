using System.Security.Claims;

namespace Cinora.Web.Security;

/// <summary>
/// The pure, side-effect-free decision that gates the admin-only Hangfire <c>/jobs</c> dashboard (Milestone 5.3,
/// ADR 0018 §5, <c>security-hardening</c>). Isolated from the Hangfire <see cref="Hangfire.Dashboard"/> types so
/// it is trivially unit-testable, and used by the thin <see cref="AdminDashboardAuthorizationFilter"/> adapter.
/// <para>
/// <b>Fail-closed.</b> Anonymous / unauthenticated callers are always denied. An authenticated caller is admitted
/// only if their <b>server-assigned user-id</b> (<see cref="ClaimTypes.NameIdentifier"/> — ASP.NET Identity sets
/// it to the <c>ApplicationUser.Id</c> GUID) OR their <b>email</b> claim matches the configured allow-list
/// (case-insensitive, via the supplied set's comparer), or — in the <c>Development</c> environment only — the
/// "any authenticated user" convenience flag is enabled. With no admins configured and outside Development,
/// <em>every</em> caller is denied. The free-form name claims (<c>Identity.Name</c> / <see cref="ClaimTypes.Name"/>)
/// are deliberately NOT matched: they are not server-minted identifiers and would couple authZ to the
/// <c>UserName == Email</c> invariant.
/// </para>
/// <para>
/// <b>Allow-list entry guidance.</b> An <em>email</em> entry must belong to an already-registered, controlled
/// account: because registration runs with <c>RequireConfirmedAccount=false</c>, an unverified or pre-provisioned
/// email is a narrow escalation risk. A <em>user-id GUID</em> entry is unforgeable (the server mints it), so an
/// operator wanting maximal assurance should list the user-id rather than the email.
/// </para>
/// </summary>
public static class AdminAccessPolicy
{
    /// <summary>Decides whether the supplied principal may access the admin dashboard.</summary>
    /// <param name="user">The request principal, or <see langword="null"/> when there is none.</param>
    /// <param name="isDevelopment">Whether the host is running in the <c>Development</c> environment.</param>
    /// <param name="adminAccounts">
    /// The allow-listed account identifiers — each a server-assigned user-id GUID or an email (never a free-form
    /// name). Build this set with a case-insensitive comparer (<see cref="StringComparer.OrdinalIgnoreCase"/>) —
    /// the match is a plain <c>Contains</c>.
    /// </param>
    /// <param name="allowAnyAuthenticatedInDevelopment">
    /// When <see langword="true"/> AND <paramref name="isDevelopment"/> is <see langword="true"/>, any
    /// authenticated user is admitted (a local-dev convenience). Ignored outside Development.
    /// </param>
    /// <returns><see langword="true"/> if access is authorized; otherwise <see langword="false"/>.</returns>
    public static bool IsAuthorized(
        ClaimsPrincipal? user,
        bool isDevelopment,
        IReadOnlySet<string> adminAccounts,
        bool allowAnyAuthenticatedInDevelopment)
    {
        ArgumentNullException.ThrowIfNull(adminAccounts);

        // Fail-closed: anonymous / unauthenticated callers are always denied.
        if (user?.Identity is not { IsAuthenticated: true })
        {
            return false;
        }

        // An explicitly allow-listed account (matched on the server-assigned user-id or the email claim,
        // case-insensitively via the set's comparer) is an admin.
        foreach (var identifier in CandidateIdentifiers(user))
        {
            if (adminAccounts.Contains(identifier))
            {
                return true;
            }
        }

        // Development-only convenience: optionally treat ANY authenticated user as an admin. Never loosens
        // Testing or Production, because isDevelopment is false there.
        if (isDevelopment && allowAnyAuthenticatedInDevelopment)
        {
            return true;
        }

        // Default deny: a non-allow-listed authenticated user outside the dev convenience (and the no-admins-
        // configured Production default) reaches here.
        return false;
    }

    // The identifiers an authenticated principal may be matched against: its server-assigned user-id
    // (ClaimTypes.NameIdentifier — the unforgeable ApplicationUser.Id GUID) and its email claim. The free-form
    // name claims (Identity.Name / ClaimTypes.Name) are deliberately excluded (L1/L2 hardening). Blank values are
    // skipped.
    private static IEnumerable<string> CandidateIdentifiers(ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            yield return userId;
        }

        var email = user.FindFirstValue(ClaimTypes.Email);
        if (!string.IsNullOrWhiteSpace(email))
        {
            yield return email;
        }
    }
}
