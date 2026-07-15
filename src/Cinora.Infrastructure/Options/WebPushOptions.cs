using System.ComponentModel.DataAnnotations;

namespace Cinora.Infrastructure.Options;

/// <summary>
/// Binds the <c>WebPush</c> configuration section carrying the self-generated VAPID credentials the
/// <c>WebPushSender</c> uses to send Web Push messages (Milestone 6.2, ADR 0020). All three fields are
/// <b>optional</b>: Web Push is strictly additive, so a deployer without VAPID keys still runs — push simply
/// stays OFF (<see cref="IsConfigured"/> is <see langword="false"/>, the sender no-ops, and
/// <c>GET /push/public-key</c> reports unconfigured). The <b>private key is a secret</b> and MUST come from
/// user-secrets / environment variables — <b>never</b> <c>appsettings.*.json</c> (the public key is non-secret
/// and may live in appsettings). Bound with <see cref="OptionsValidationExtensions.ValidateUsingDataAnnotations"/>
/// (lazy) so a malformed value that IS present fails; it is deliberately <b>not</b> hard-<c>ValidateOnStart</c>-ed
/// on missing keys — an absent VAPID configuration degrades push to off, it does not crash boot (design §8).
/// </summary>
public sealed class WebPushOptions
{
    /// <summary>The configuration section name bound to this options type.</summary>
    public const string SectionName = "WebPush";

    /// <summary>
    /// The VAPID subject — a <c>mailto:</c> address or an <c>https://</c> origin URL identifying the sender, as
    /// required by RFC 8292. Optional (push off when absent); validated for shape only when present.
    /// </summary>
    [RegularExpression(
        @"^(mailto:.+|https://.+)$",
        ErrorMessage = "WebPush:Subject must be a 'mailto:' address or an 'https://' URL when configured.")]
    public string? Subject { get; set; }

    /// <summary>
    /// The VAPID public key (URL-safe base64). Non-secret — it is served to the browser at
    /// <c>GET /push/public-key</c> and passed as the subscription's <c>applicationServerKey</c>. Optional.
    /// </summary>
    [RegularExpression(
        "^[A-Za-z0-9_-]+$",
        ErrorMessage = "WebPush:PublicKey must be URL-safe base64 (base64url) when configured.")]
    public string? PublicKey { get; set; }

    /// <summary>
    /// The VAPID private key (URL-safe base64). <b>Secret</b> — user-secrets / environment only, never
    /// appsettings. Optional; validated for shape only when present. Never logged.
    /// </summary>
    [RegularExpression(
        "^[A-Za-z0-9_-]+$",
        ErrorMessage = "WebPush:PrivateKey must be URL-safe base64 (base64url) when configured.")]
    public string? PrivateKey { get; set; }

    /// <summary>
    /// Whether a complete VAPID credential set is present (subject + public + private key). When
    /// <see langword="false"/>, Web Push is OFF: the sender no-ops and the subscribe UI stays hidden — the app
    /// still delivers in-app notifications (inbox + SignalR), so nothing breaks without push (degrade-if-absent).
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Subject)
        && !string.IsNullOrWhiteSpace(PublicKey)
        && !string.IsNullOrWhiteSpace(PrivateKey);
}
