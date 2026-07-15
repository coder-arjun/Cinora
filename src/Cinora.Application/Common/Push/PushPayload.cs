namespace Cinora.Application.Common.Push;

/// <summary>
/// The Application-owned Web Push message body the <see cref="Interfaces.IPushSender"/> serializes and sends
/// (ADR 0020 §3.4). All text is treated as <b>plain text</b> by the service worker and set via
/// <c>showNotification</c>'s <c>title</c>/<c>body</c> options — never <c>innerHTML</c> — because
/// <see cref="Body"/> carries the server-composed notification message (which contains a user-controlled
/// display name; XSS stance §3).
/// </summary>
/// <param name="Title">The notification title (e.g. <c>"Cinora"</c>).</param>
/// <param name="Body">The notification body — the plain-text notification message.</param>
/// <param name="Url">The same-origin relative deep-link the click opens, or <c>null</c> (falls back to <c>"/"</c>).</param>
/// <param name="Tag">A coalescing tag (the notification type name) so repeats replace rather than stack, or <c>null</c>.</param>
public sealed record PushPayload(string Title, string Body, string? Url, string? Tag);
