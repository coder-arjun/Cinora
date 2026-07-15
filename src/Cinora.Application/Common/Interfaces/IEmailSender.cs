namespace Cinora.Application.Common.Interfaces;

/// <summary>
/// Sends a transactional email (currently the password-reset link). The Application-owned port keeps the SMTP
/// vendor (MailKit/MimeKit) behind an Infrastructure adapter, honouring the Clean Architecture dependency rule.
/// The adapter degrades gracefully — when SMTP is not configured it logs and no-ops rather than throwing — so the
/// forgot-password flow never surfaces a 500 or reveals whether an address exists.
/// </summary>
public interface IEmailSender
{
    /// <summary>Sends an HTML email to a single recipient. Never throws for an unconfigured mailer (it no-ops).</summary>
    /// <param name="toEmail">The recipient's email address.</param>
    /// <param name="subject">The message subject.</param>
    /// <param name="htmlBody">The HTML message body (already composed/encoded by the caller).</param>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    /// <returns>A task that completes when the message has been sent (or skipped when unconfigured).</returns>
    Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default);
}
