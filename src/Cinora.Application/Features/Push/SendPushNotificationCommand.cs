using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Push;
using Cinora.Application.Features.Notifications;
using Cinora.Domain.Enums;
using Cinora.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinora.Application.Features.Push;

/// <summary>
/// Fans one already-committed notification out to the recipient's Web Push devices (Milestone 6.2, ADR 0020
/// §3.4). It runs <b>off the request critical path</b> — dispatched by <c>IPushDispatch</c> in its own DI scope,
/// keyed only by the notification id — so a slow/flaky push service never adds latency to, or can fail, the
/// write that produced the notification (the inbox row is authoritative; N2/ADR 0011). It reloads the enriched
/// notification via the shared <see cref="NotificationProjection"/> (recipient + type + message + the review
/// coordinates for the deep link), builds one <see cref="PushPayload"/> whose <c>data.url</c> comes from the
/// single <see cref="NotificationDeepLink"/> grammar, and sends to every device. A <see cref="PushSendResult.Gone"/>
/// (404/410) device is deleted on the spot (self-healing cleanup); a <see cref="PushSendResult.TransientFailure"/>
/// is logged and skipped. <b>Milestone 6.3 gates push per type</b> (ADR 0020 §5.2): before loading devices it
/// reads the recipient's <see cref="NotificationPreferences"/> and returns early if this type is muted — the in-app
/// inbox row + SignalR toast already fired unconditionally in the producing handler; ONLY the out-of-app push is
/// gated here, in this one place.
/// </summary>
/// <param name="NotificationId">The id of the committed notification to push.</param>
public sealed record SendPushNotificationCommand(Guid NotificationId) : IRequest<Unit>;

/// <summary>
/// Handles <see cref="SendPushNotificationCommand"/>. Best-effort by construction: it is invoked inside the
/// <c>InlinePushDispatch</c> scope (its own try/catch), so it can surface a fault to that boundary without ever
/// affecting the originating write.
/// </summary>
/// <param name="db">The persistence context — reads the notification/devices, deletes dead subscriptions.</param>
/// <param name="pushSender">The Web Push port (the WebPushSender adapter at runtime).</param>
/// <param name="logger">Records a pruned dead device and any transient send failure.</param>
public sealed partial class SendPushNotificationCommandHandler(
    IAppDbContext db,
    IPushSender pushSender,
    ILogger<SendPushNotificationCommandHandler> logger)
    : IRequestHandler<SendPushNotificationCommand, Unit>
{
    private const string PushTitle = "Cinora";

    /// <inheritdoc />
    public async Task<Unit> Handle(SendPushNotificationCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Milestone 6.4 (§6.3): reload everything the fan-out needs in ONE round-trip via the dedicated lean push
        // projection — recipient id + type + message + review-target coords for the deep link + the recipient's
        // per-type push flags (folding what used to be three reads). The realtime DTO carries no review
        // coordinates, so a fresh read is still required here.
        var row = await db.Notifications
            .AsNoTracking()
            .Where(notification => notification.Id == request.NotificationId)
            .Select(NotificationProjection.ToPushRow(db))
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return Unit.Value; // the notification was removed before the dispatch ran — nothing to push.
        }

        // Preference gate (Milestone 6.3, ADR 0020 §5.2): return early if THIS type is muted for push. The in-app
        // inbox row + SignalR live toast already fired unconditionally in the producing handler; only the
        // out-of-app push fan-out is gated. A missing recipient row (no user — not a production case) defaults to
        // enabled (RecipientPreferences is null). Reuse the domain VO's IsPushEnabled from the projected flags.
        if (row.RecipientPreferences is { } prefs)
        {
            var preferences = new NotificationPreferences(
                prefs.PushFriendRequests, prefs.PushFriendAccepted, prefs.PushReviewLikes, prefs.PushComments);
            if (!preferences.IsPushEnabled(row.Type))
            {
                LogPushMutedByPreference(logger, row.RecipientUserId, row.Type);
                return Unit.Value;
            }
        }

        // Tracked so a Gone subscription can be deleted in place.
        var devices = await db.Devices
            .Where(device => device.UserId == row.RecipientUserId)
            .ToListAsync(cancellationToken);

        if (devices.Count == 0)
        {
            return Unit.Value; // the recipient has no push subscriptions.
        }

        var url = NotificationDeepLink.Resolve(
            row.Type, row.ReviewTarget?.TmdbId, row.ReviewTarget?.MediaType, row.ActorUserId);
        var payload = new PushPayload(PushTitle, row.Message, url, row.Type.ToString());

        var pruned = false;
        foreach (var device in devices)
        {
            var subscription = new PushSubscription(device.Endpoint, device.P256dhKey, device.AuthSecret);

            PushSendResult result;
            try
            {
                result = await pushSender.SendAsync(subscription, payload, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Defense-in-depth (6.2 code Low): a single malformed device (e.g. bad keys) must never abort the
                // batch or skip the deferred Gone-prune SaveChanges below. Log and move to the next device.
                LogPushDeviceErrored(logger, exception, device.Id, row.RecipientUserId);
                continue;
            }

            switch (result)
            {
                case PushSendResult.Gone:
                    // The subscription is dead (404/410) — prune it exactly when it proves dead (ADR 0020 §3.6).
                    db.Devices.Remove(device);
                    pruned = true;
                    LogDevicePruned(logger, device.Id, row.RecipientUserId);
                    break;

                case PushSendResult.TransientFailure:
                    LogPushTransientFailure(logger, device.Id, row.RecipientUserId);
                    break;

                case PushSendResult.Delivered:
                default:
                    break;
            }
        }

        if (pruned)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return Unit.Value;
    }

    [LoggerMessage(
        EventId = 6200,
        Level = LogLevel.Information,
        Message = "Pruned dead Web Push device {DeviceId} for recipient {RecipientUserId} (push service returned Gone).")]
    private static partial void LogDevicePruned(ILogger logger, Guid deviceId, Guid recipientUserId);

    [LoggerMessage(
        EventId = 6202,
        Level = LogLevel.Debug,
        Message = "Web Push to recipient {RecipientUserId} skipped: the {NotificationType} type is muted in their " +
                  "notification preferences. The in-app inbox notification is unaffected.")]
    private static partial void LogPushMutedByPreference(
        ILogger logger, Guid recipientUserId, NotificationType notificationType);

    [LoggerMessage(
        EventId = 6203,
        Level = LogLevel.Warning,
        Message = "Web Push to device {DeviceId} for recipient {RecipientUserId} threw an unclassified error; the " +
                  "device was skipped and the rest of the batch continued.")]
    private static partial void LogPushDeviceErrored(
        ILogger logger, Exception exception, Guid deviceId, Guid recipientUserId);

    [LoggerMessage(
        EventId = 6201,
        Level = LogLevel.Warning,
        Message = "Web Push to device {DeviceId} for recipient {RecipientUserId} failed transiently; the " +
                  "notification remains in the recipient's inbox.")]
    private static partial void LogPushTransientFailure(ILogger logger, Guid deviceId, Guid recipientUserId);
}
