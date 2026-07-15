namespace Cinora.Application.Common.Push;

/// <summary>
/// An Application-owned Web Push subscription: the endpoint plus the client's encryption keys, in the shape the
/// <see cref="Interfaces.IPushSender"/> port speaks (ADR 0020 §2). It is deliberately <b>not</b> the WebPush
/// library's subscription type nor the Domain <c>Device</c> entity — the adapter maps this to the vendor type
/// internally, so no vendor dependency crosses the layer boundary.
/// </summary>
/// <param name="Endpoint">The push service endpoint to deliver the message to.</param>
/// <param name="P256dh">The client's P-256 ECDH public key (base64url) used to encrypt the payload.</param>
/// <param name="Auth">The client's authentication secret (base64url) used to encrypt the payload.</param>
public sealed record PushSubscription(string Endpoint, string P256dh, string Auth);
