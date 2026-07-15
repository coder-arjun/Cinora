using Cinora.Application.Common.Push;

namespace Cinora.Application.Common.Interfaces;

/// <summary>
/// The Application port for sending a single Web Push message (ADR 0020 §2). It speaks Application-owned
/// primitives only (<see cref="PushSubscription"/> + <see cref="PushPayload"/>) — <b>never</b> the WebPush
/// library type or a <c>Device</c> entity on the wire — so the encryption/VAPID adapter (Infrastructure's
/// <c>WebPushSender</c>) can be swapped for a future paid gateway with no change above the port. The result
/// distinguishes a delivered message from a dead subscription (to prune) and a transient failure (to retry/skip).
/// </summary>
public interface IPushSender
{
    /// <summary>Sends one push message to one subscription, best-effort.</summary>
    /// <param name="subscription">The target subscription (endpoint + encryption keys).</param>
    /// <param name="payload">The message to encrypt and deliver.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>The send outcome: delivered, gone (prune), or a transient failure.</returns>
    Task<PushSendResult> SendAsync(
        PushSubscription subscription, PushPayload payload, CancellationToken cancellationToken);
}
