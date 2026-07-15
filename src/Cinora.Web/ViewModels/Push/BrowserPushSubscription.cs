namespace Cinora.Web.ViewModels.Push;

/// <summary>
/// The browser <c>PushSubscription</c> JSON shape a <c>fetch("/push/subscribe")</c> body carries (Milestone 6.2,
/// ADR 0020 §3.1) — <c>{ endpoint, expirationTime, keys: { p256dh, auth } }</c>. It is a Web-layer transport model
/// only: <see cref="Cinora.Web.Controllers.PushController"/> unwraps it into the Application
/// <c>RegisterDeviceCommand</c> so the browser type never crosses into Application. Property matching is
/// case-insensitive (MVC's System.Text.Json), so the browser's lowercase keys bind here.
/// </summary>
public sealed class BrowserPushSubscription
{
    /// <summary>The push service endpoint the browser minted.</summary>
    public string? Endpoint { get; set; }

    /// <summary>The subscription's encryption keys (<c>p256dh</c> + <c>auth</c>).</summary>
    public BrowserPushKeys? Keys { get; set; }
}

/// <summary>The encryption key pair of a browser <c>PushSubscription</c>.</summary>
public sealed class BrowserPushKeys
{
    /// <summary>The client's P-256 ECDH public key (base64url).</summary>
    public string? P256dh { get; set; }

    /// <summary>The client's authentication secret (base64url).</summary>
    public string? Auth { get; set; }
}

/// <summary>The body of a <c>fetch("/push/unsubscribe")</c> request — the endpoint of the device to remove.</summary>
public sealed class PushUnsubscribeRequest
{
    /// <summary>The push service endpoint of the subscription to remove.</summary>
    public string? Endpoint { get; set; }
}
