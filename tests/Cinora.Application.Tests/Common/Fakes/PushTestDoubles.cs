using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Push;
using Cinora.Application.Common.Realtime;

namespace Cinora.Application.Tests.Common.Fakes;

/// <summary>
/// A hand-rolled <see cref="IPushSender"/> that records every send and returns a configurable result (default
/// <see cref="PushSendResult.Delivered"/>). Lets the send-handler tests capture the built <see cref="PushPayload"/>
/// (title/body/url/tag) and drive the Gone-prune / transient-skip branches. No mocking package (consistent with
/// the rest of the suite + Central Package Management).
/// </summary>
internal sealed class FakePushSender : IPushSender
{
    /// <summary>The result every <see cref="SendAsync"/> returns (override to exercise Gone / TransientFailure).</summary>
    public PushSendResult Result { get; set; } = PushSendResult.Delivered;

    /// <summary>Each (subscription, payload) pair passed to <see cref="SendAsync"/>, in call order.</summary>
    public List<(PushSubscription Subscription, PushPayload Payload)> Sent { get; } = [];

    /// <summary>
    /// Endpoints whose <see cref="SendAsync"/> throws an unclassified exception BEFORE recording the send (the
    /// 6.2 malformed-device code Low): the send handler must catch it, skip that device, and keep the batch going.
    /// </summary>
    public HashSet<string> ThrowForEndpoints { get; } = new(StringComparer.Ordinal);

    public Task<PushSendResult> SendAsync(
        PushSubscription subscription, PushPayload payload, CancellationToken cancellationToken)
    {
        if (ThrowForEndpoints.Contains(subscription.Endpoint))
        {
            throw new InvalidOperationException($"Simulated malformed device for endpoint '{subscription.Endpoint}'.");
        }

        Sent.Add((subscription, payload));
        return Task.FromResult(Result);
    }
}

/// <summary>
/// A hand-rolled <see cref="IPushDispatch"/> that records the notification ids it was asked to enqueue, so a
/// handler/seam test can assert the fan-out fired exactly once (or not at all) without a background task running.
/// </summary>
internal sealed class FakePushDispatch : IPushDispatch
{
    /// <summary>The notification ids passed to <see cref="Enqueue"/>, in call order.</summary>
    public List<Guid> Enqueued { get; } = [];

    /// <summary>The chat message ids passed to <see cref="EnqueueChatMessage"/>, in call order.</summary>
    public List<Guid> EnqueuedChatMessages { get; } = [];

    public void Enqueue(Guid notificationId) => Enqueued.Add(notificationId);

    public void EnqueueChatMessage(Guid messageId) => EnqueuedChatMessages.Add(messageId);
}

/// <summary>A no-op <see cref="IRealtimeNotifier"/> that records the recipients it was asked to notify.</summary>
internal sealed class FakeRealtimeNotifier : IRealtimeNotifier
{
    /// <summary>The recipients passed to <see cref="NotifyAsync"/>, in call order.</summary>
    public List<Guid> Notified { get; } = [];

    public Task NotifyAsync(Guid recipientUserId, NotificationDto notification, CancellationToken cancellationToken)
    {
        Notified.Add(recipientUserId);
        return Task.CompletedTask;
    }

    public Task UnreadCountChangedAsync(Guid recipientUserId, int unreadCount, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
