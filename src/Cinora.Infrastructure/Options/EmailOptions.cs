using System.ComponentModel.DataAnnotations;

namespace Cinora.Infrastructure.Options;

/// <summary>
/// Binds the <c>Email</c> configuration section — the SMTP mailer used for the password-reset link. The key names
/// mirror the sibling app so the same Gmail App-Password setup carries over. It defaults to <b>disabled</b>: with
/// <see cref="Enabled"/> false (or a blank <see cref="User"/>/<see cref="Password"/>) the adapter no-ops and logs,
/// so the app boots and the forgot-password flow degrades gracefully. <see cref="Password"/> is a secret — supply
/// it via user-secrets (dev) or an environment variable (deploy), NEVER <c>appsettings.json</c>. Bound with
/// <c>ValidateUsingDataAnnotations</c> (lazy) but NOT <c>ValidateOnStart</c>, since the feature is optional.
/// </summary>
public sealed class EmailOptions
{
    /// <summary>The configuration section name bound to this options type.</summary>
    public const string SectionName = "Email";

    /// <summary>Master switch. When false the mailer no-ops (and logs) — the app runs without email.</summary>
    public bool Enabled { get; set; }

    /// <summary>The SMTP host. Defaults to Gmail's submission host.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Host { get; set; } = "smtp.gmail.com";

    /// <summary>The SMTP submission port (587 = STARTTLS).</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    /// <summary>The SMTP account/user (e.g. the Gmail address). Secret-ish — keep out of appsettings.</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>The SMTP password (a Gmail 16-char App Password). SECRET — user-secrets / env var only.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>The From address; defaults to <see cref="User"/> when blank.</summary>
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>The friendly From name shown in mail clients.</summary>
    [Required(AllowEmptyStrings = false)]
    public string FromName { get; set; } = "Cinora";

    /// <summary>True only when the mailer is switched on and has both a user and a password.</summary>
    public bool IsConfigured =>
        Enabled && !string.IsNullOrWhiteSpace(User) && !string.IsNullOrWhiteSpace(Password);
}
