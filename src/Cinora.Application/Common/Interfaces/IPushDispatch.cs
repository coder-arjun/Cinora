namespace Cinora.Application.Common.Interfaces;

/// <summary>
/// The Application port that fans a just-committed notification out to Web Push, <b>off the request critical
/// path</b> (ADR 0020 §3.4). The ADR-0011 post-commit best-effort seam calls
/// <see cref="Enqueue"/> after the SignalR push; the implementation dispatches the actual send elsewhere (its own
/// DI scope on a background task now — <c>InlinePushDispatch</c>; a durable Hangfire job later), so a slow or
/// flaky push endpoint never adds latency to — or can fail — the write. The inbox row is authoritative; a dropped
/// push is a missed out-of-app nudge, never a missed notification (N2/ADR 0011).
/// </summary>
public interface IPushDispatch
{
    /// <summary>
    /// Enqueues a best-effort Web Push fan-out for the already-committed notification. Returns immediately —
    /// the send runs off the caller's thread and scope, and any failure is swallowed and logged (fire-and-forget).
    /// </summary>
    /// <param name="notificationId">The id of the committed notification to push.</param>
    void Enqueue(Guid notificationId);

    /// <summary>
    /// Enqueues a best-effort Web Push fan-out for a just-committed chat MESSAGE — the "new message" alert in the
    /// recipients' mobile notification panel. Returns immediately; the send runs off the caller's thread and scope,
    /// keyed only by the message id, and any failure is swallowed and logged (fire-and-forget).
    /// </summary>
    /// <param name="messageId">The id of the committed chat message to push to its recipients.</param>
    void EnqueueChatMessage(Guid messageId);
}
