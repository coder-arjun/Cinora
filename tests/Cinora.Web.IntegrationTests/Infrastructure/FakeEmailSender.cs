using Cinora.Application.Common.Interfaces;

namespace Cinora.Web.IntegrationTests.Infrastructure;

/// <summary>
/// Captures the emails the app would send (registered via <c>ConfigureTestServices</c> in place of the real SMTP
/// adapter) so a test can assert an email was/was not sent and pull the password-reset link out of the body — no
/// SMTP server involved.
/// </summary>
internal sealed class FakeEmailSender : IEmailSender
{
    private readonly List<CapturedEmail> _sent = [];

    /// <summary>Every captured email, in send order.</summary>
    public IReadOnlyList<CapturedEmail> Sent => _sent;

    /// <summary>The most recently captured email, or null if none were sent.</summary>
    public CapturedEmail? Last => _sent.Count > 0 ? _sent[^1] : null;

    /// <inheritdoc />
    public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        _sent.Add(new CapturedEmail(toEmail, subject, htmlBody));
        return Task.CompletedTask;
    }

    /// <summary>A single captured email.</summary>
    /// <param name="To">The recipient address.</param>
    /// <param name="Subject">The subject line.</param>
    /// <param name="HtmlBody">The HTML body (the reset link lives in here).</param>
    internal sealed record CapturedEmail(string To, string Subject, string HtmlBody);
}
