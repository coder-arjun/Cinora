using System.ComponentModel.DataAnnotations;

namespace Cinora.Infrastructure.Options;

/// <summary>
/// Binds the <c>AdminDashboard</c> configuration section describing who may reach the admin-gated Hangfire
/// <c>/jobs</c> dashboard (Milestone 5.3, ADR 0018 §5). Infrastructure-owned (like all Options); the values reach
/// the Web-layer <c>AdminDashboardAuthorizationFilter</c> via the resolved options at <c>MapHangfireDashboard</c>
/// time. Bound with <c>ValidateUsingDataAnnotations().ValidateOnStart()</c> — the dashboard is a real attack
/// surface, so its access configuration is validated at boot.
/// <para>
/// <b>Fail-closed by default.</b> Out of the box <see cref="AdminAccounts"/> is empty and
/// <see cref="AllowAnyAuthenticatedInDevelopment"/> is <see langword="false"/>, so — in every environment,
/// including Production — the authorization filter denies <em>every</em> caller (anonymous and authenticated
/// alike) until an operator explicitly opts specific accounts in via configuration.
/// </para>
/// </summary>
public sealed class AdminDashboardOptions
{
    /// <summary>The configuration section name bound to this options type.</summary>
    public const string SectionName = "AdminDashboard";

    /// <summary>
    /// The allow-list of account identifiers permitted to reach the Hangfire dashboard. Each entry is a
    /// <b>server-assigned user-id GUID</b> (the unforgeable, preferred form) or an <b>email</b> — never a free-form
    /// name claim. Matched case-insensitively. Empty (the default) means <em>no</em> admins — everyone is denied.
    /// (Config key was <c>AdminEmails</c> before Milestone 5.3's L1/L2 hardening — the match is now user-id/email.)
    /// </summary>
    [Required]
    public string[] AdminAccounts { get; set; } = [];

    /// <summary>
    /// When <see langword="true"/>, ANY authenticated user may reach the dashboard — but ONLY in the
    /// <c>Development</c> environment (never <c>Testing</c> or <c>Production</c>). A local-dev convenience;
    /// defaults to <see langword="false"/> so it never loosens a deployed environment.
    /// </summary>
    public bool AllowAnyAuthenticatedInDevelopment { get; set; }
}
