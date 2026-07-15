using Microsoft.Extensions.Logging;

namespace Cinora.Infrastructure.Email;

/// <summary>
/// Source-generated, high-performance log messages for <see cref="SmtpEmailSender"/> (CA1848 — arguments are
/// evaluated only when the level is enabled). Metadata only — the subject and recipient address — NEVER the
/// message body or the SMTP credentials. The Development-only action-link message is emitted by the caller only in
/// Development, so a reset link never reaches a Production log.
/// </summary>
internal static partial class EmailLog
{
    [LoggerMessage(
        EventId = 6300,
        Level = LogLevel.Warning,
        Message = "Email '{Subject}' to {Email} was NOT sent — SMTP is not configured (set Email:Enabled/User/Password).")]
    public static partial void NotConfigured(ILogger logger, string subject, string email);

    [LoggerMessage(
        EventId = 6301,
        Level = LogLevel.Warning,
        Message = "[DEV] Action link for '{Subject}': {Link}")]
    public static partial void DevActionLink(ILogger logger, string subject, string link);

    [LoggerMessage(
        EventId = 6302,
        Level = LogLevel.Information,
        Message = "Email '{Subject}' sent to {Email}.")]
    public static partial void Sent(ILogger logger, string subject, string email);

    [LoggerMessage(
        EventId = 6303,
        Level = LogLevel.Error,
        Message = "Failed to send email '{Subject}' to {Email}.")]
    public static partial void Failed(ILogger logger, Exception exception, string subject, string email);
}
