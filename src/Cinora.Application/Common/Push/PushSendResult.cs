namespace Cinora.Application.Common.Push;

/// <summary>
/// The outcome of a single <see cref="Interfaces.IPushSender.SendAsync"/> attempt (ADR 0020 §2). The dispatcher
/// uses it to self-heal: a <see cref="Gone"/> subscription (the push service returned 404/410) is deleted, while
/// a <see cref="TransientFailure"/> is logged and skipped.
/// </summary>
public enum PushSendResult
{
    /// <summary>The push service accepted the message (2xx).</summary>
    Delivered = 0,

    /// <summary>The subscription is dead (HTTP 404/410) — the owning <c>Device</c> should be pruned.</summary>
    Gone = 1,

    /// <summary>A transient/other failure (timeout, 5xx, network, or push disabled) — log and move on.</summary>
    TransientFailure = 2,
}
