using System.Text.RegularExpressions;
using Cinora.Application.Common.Interfaces;
using Cinora.Infrastructure.Options;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Cinora.Infrastructure.Email;

/// <summary>
/// The <see cref="IEmailSender"/> adapter: sends HTML mail over SMTP with MailKit (STARTTLS on the submission
/// port), configured for Gmail by default but working with any SMTP host. It reads <see cref="EmailOptions"/> and
/// deliberately DEGRADES rather than throws — when the mailer is not configured it logs a Warning and returns, so
/// the forgot-password flow never 500s and never reveals whether an address exists. As a Development convenience,
/// when mail is off it also surfaces the action link (the reset URL) in the logs so the flow can be exercised with
/// no mail server; this is guarded to Development and never leaks links in Production. Internal — the composition
/// root registers it behind the Application port; the MimeKit/MailKit types never cross the dependency rule.
/// </summary>
internal sealed partial class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<SmtpEmailSender> _logger;

    /// <summary>Creates the sender over the bound email options, the host environment, and a logger.</summary>
    /// <param name="options">The bound <see cref="EmailOptions"/> (host, port, credentials, from).</param>
    /// <param name="environment">The host environment, used to guard the Development link-surfacing convenience.</param>
    /// <param name="logger">The logger used for send/degrade diagnostics (never logs credentials).</param>
    public SmtpEmailSender(
        IOptions<EmailOptions> options,
        IHostEnvironment environment,
        ILogger<SmtpEmailSender> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task SendAsync(
        string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toEmail);

        if (!_options.IsConfigured)
        {
            EmailLog.NotConfigured(_logger, subject, toEmail);

            // Development-only convenience: surface the reset link so the flow works with no mail server.
            if (_environment.IsDevelopment())
            {
                var match = HrefPattern().Match(htmlBody);
                if (match.Success)
                {
                    EmailLog.DevActionLink(_logger, subject, System.Net.WebUtility.HtmlDecode(match.Groups["u"].Value));
                }
            }

            return;
        }

        var message = new MimeMessage();
        var from = string.IsNullOrWhiteSpace(_options.FromAddress) ? _options.User : _options.FromAddress;
        message.From.Add(new MailboxAddress(_options.FromName, from));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(_options.Host, _options.Port, SecureSocketOptions.StartTls, cancellationToken);
            await client.AuthenticateAsync(_options.User, _options.Password, cancellationToken);
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
            EmailLog.Sent(_logger, subject, toEmail);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A transport/auth failure must not surface as a 500 or reveal address existence: log and swallow, so
            // the caller still returns the neutral "if the address exists, we've sent a link" confirmation.
            EmailLog.Failed(_logger, exception, subject, toEmail);
        }
    }

    // Extracts the first href value from the HTML body (Development link-surfacing only).
    [GeneratedRegex("href=[\"'](?<u>[^\"']+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefPattern();
}
