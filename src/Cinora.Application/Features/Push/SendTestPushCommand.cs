using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinora.Application.Features.Push;

/// <summary>
/// Sends a one-off TEST Web Push to the CURRENT user's own devices so they can confirm notifications work on this
/// device (the opt-in bar's "Send test"). The actor is resolved server-side from <see cref="ICurrentUser"/> — the
/// command binds no user id. Mirrors <see cref="SendPushNotificationCommand"/>'s send + self-healing prune of a
/// <see cref="PushSendResult.Gone"/> device, but with a fixed canned payload, no notification row, and no per-type
/// preference gate (a test is a deliberate user action, not an event fan-out).
/// </summary>
public sealed record SendTestPushCommand : IRequest<TestPushResult>;

/// <summary>The outcome of a test push: how many devices the user has, and how many the service accepted.</summary>
/// <param name="DeviceCount">The number of the user's registered push devices at send time.</param>
/// <param name="Delivered">How many devices the push service accepted the message for.</param>
public sealed record TestPushResult(int DeviceCount, int Delivered);

/// <summary>Handles <see cref="SendTestPushCommand"/> by sending a canned payload to every device the user has.</summary>
/// <param name="db">The persistence context — reads the user's devices and prunes a dead subscription.</param>
/// <param name="currentUser">The server-resolved acting user (ADR 0009).</param>
/// <param name="pushSender">The Web Push port (the WebPushSender adapter at runtime).</param>
/// <param name="logger">Records a device that errored so one bad subscription never aborts the batch.</param>
public sealed partial class SendTestPushCommandHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    IPushSender pushSender,
    ILogger<SendTestPushCommandHandler> logger)
    : IRequestHandler<SendTestPushCommand, TestPushResult>
{
    private const string TestTitle = "Cinora";
    private const string TestBody = "Test notification — push is working on this device.";

    /// <inheritdoc />
    public async Task<TestPushResult> Handle(SendTestPushCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.GetRequiredUserId();

        // Tracked so a Gone subscription can be deleted in place (self-healing, like SendPushNotificationCommand).
        var devices = await db.Devices
            .Where(device => device.UserId == userId)
            .ToListAsync(cancellationToken);

        if (devices.Count == 0)
        {
            return new TestPushResult(0, 0);
        }

        var payload = new PushPayload(TestTitle, TestBody, "/", "test");

        var delivered = 0;
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
                // One malformed device must never abort the batch nor skip the deferred prune below.
                LogTestDeviceErrored(logger, exception, device.Id, userId);
                continue;
            }

            switch (result)
            {
                case PushSendResult.Gone:
                    db.Devices.Remove(device);
                    pruned = true;
                    break;

                case PushSendResult.Delivered:
                    delivered += 1;
                    break;

                case PushSendResult.TransientFailure:
                default:
                    break;
            }
        }

        if (pruned)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return new TestPushResult(devices.Count, delivered);
    }

    [LoggerMessage(
        EventId = 6210,
        Level = LogLevel.Warning,
        Message = "Test Web Push to device {DeviceId} for user {UserId} threw an unclassified error; the device " +
                  "was skipped and the rest of the batch continued.")]
    private static partial void LogTestDeviceErrored(
        ILogger logger, Exception exception, Guid deviceId, Guid userId);
}
