using Hangfire.Dashboard;

namespace Cinora.Web.Security;

/// <summary>
/// The thin Hangfire adapter that gates the <c>/jobs</c> dashboard (Milestone 5.3, ADR 0018 §5). It reads the
/// request principal off the dashboard context and delegates the actual decision to the pure, unit-tested
/// <see cref="AdminAccessPolicy"/>. Constructed at <c>MapHangfireDashboard</c> time with the resolved admin set +
/// the environment, so it holds no framework services.
/// <para>
/// The dashboard is terminal Hangfire middleware, not an MVC endpoint, so it does NOT inherit the app's global
/// fail-closed fallback authorization policy — this filter is the sole gate. Hangfire returns <c>401</c> when
/// <see cref="Authorize"/> returns <see langword="false"/>, which is the fail-closed behavior for anonymous and
/// non-admin callers.
/// </para>
/// </summary>
/// <param name="adminAccounts">The allow-listed account identifiers — user-id GUIDs or emails (case-insensitive set).</param>
/// <param name="isDevelopment">Whether the host is running in the <c>Development</c> environment.</param>
/// <param name="allowAnyAuthenticatedInDevelopment">Whether any authenticated user is admitted in Development.</param>
internal sealed class AdminDashboardAuthorizationFilter(
    IReadOnlySet<string> adminAccounts,
    bool isDevelopment,
    bool allowAnyAuthenticatedInDevelopment) : IDashboardAuthorizationFilter
{
    /// <summary>Authorizes a dashboard request by delegating to <see cref="AdminAccessPolicy"/>.</summary>
    /// <param name="context">The Hangfire dashboard request context.</param>
    /// <returns><see langword="true"/> to allow the request; otherwise <see langword="false"/> (Hangfire → 401).</returns>
    public bool Authorize(DashboardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var httpContext = context.GetHttpContext();
        return AdminAccessPolicy.IsAuthorized(
            httpContext?.User, isDevelopment, adminAccounts, allowAnyAuthenticatedInDevelopment);
    }
}
