using System.ComponentModel.DataAnnotations;

namespace Cinora.Infrastructure.Options;

/// <summary>
/// Binds the <c>GoogleAuth</c> configuration section that supplies the Google OAuth client
/// credentials. Values come from user-secrets in development and environment variables / Key Vault
/// in production — never from committed <c>appsettings*.json</c>. When either credential is blank,
/// Google sign-in is left unconfigured so the application still boots and email/password login works
/// (solution-structure.md §6).
/// </summary>
public sealed class GoogleAuthOptions
{
    /// <summary>The configuration section name bound to this options type.</summary>
    public const string SectionName = "GoogleAuth";

    /// <summary>The Google OAuth client id.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>The Google OAuth client secret.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Whether both credentials are present. When <c>false</c>, <c>AddGoogle</c> is not wired and only
    /// email/password authentication is available.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
