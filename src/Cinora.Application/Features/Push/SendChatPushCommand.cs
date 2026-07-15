using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Push;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinora.Application.Features.Push;

/// <summary>
/// Fans a just-committed chat message out to the recipients' Web Push devices — the "mobile notification panel"
/// alert for a new message (like WhatsApp/Instagram). It runs <b>off the request critical path</b> (dispatched by
/// <c>IPushDispatch.EnqueueChatMessage</c> in its own DI scope, keyed only by the message id), so a slow/flaky
/// push service never adds latency to, or can fail, the send. Recipients are the conversation's members EXCEPT the
/// sender; the notification title is the sender's name (with the group name for a group), the body is a short
/// preview (or a "shared a movie" line), and the deep link opens <c>/chat/{conversationId}</c>. A dead
/// subscription (Gone) is pruned in place. Chat has no per-type mute preference, so no preference gate applies.
/// </summary>
/// <param name="MessageId">The id of the committed message to push.</param>
public sealed record SendChatPushCommand(Guid MessageId) : IRequest<Unit>;

/// <summary>Handles <see cref="SendChatPushCommand"/>. Best-effort by construction (runs inside the dispatch scope's try/catch).</summary>
/// <param name="db">The persistence context — reads the message/members/devices, prunes dead subscriptions.</param>
/// <param name="pushSender">The Web Push port (the WebPushSender adapter at runtime).</param>
/// <param name="logger">Records a pruned dead device and any per-device send fault.</param>
public sealed partial class SendChatPushCommandHandler(
    IAppDbContext db,
    IPushSender pushSender,
    ILogger<SendChatPushCommandHandler> logger)
    : IRequestHandler<SendChatPushCommand, Unit>
{
    private const int PreviewMaxLength = 120;

    /// <inheritdoc />
    public async Task<Unit> Handle(SendChatPushCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var message = await db.Messages.AsNoTracking()
            .Where(m => m.Id == request.MessageId)
            .Select(m => new
            {
                m.ConversationId,
                m.SenderId,
                m.Body,
                m.IsDeleted,
                m.SharedMovieTmdbId,
                m.SharedMovieTitle,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (message is null || message.IsDeleted)
        {
            return Unit.Value; // deleted before the dispatch ran — nothing to push.
        }

        var conversation = await db.Conversations.AsNoTracking()
            .Where(c => c.Id == message.ConversationId)
            .Select(c => new { c.Type, c.Title })
            .FirstOrDefaultAsync(cancellationToken);
        if (conversation is null)
        {
            return Unit.Value;
        }

        var senderName = await db.Users.AsNoTracking()
            .Where(u => u.Id == message.SenderId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Someone";

        // Recipients = every member EXCEPT the sender.
        var recipientIds = await db.ConversationMembers.AsNoTracking()
            .Where(m => m.ConversationId == message.ConversationId && m.UserId != message.SenderId)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
        if (recipientIds.Count == 0)
        {
            return Unit.Value;
        }

        var devices = await db.Devices
            .Where(device => recipientIds.Contains(device.UserId))
            .ToListAsync(cancellationToken); // tracked so a Gone subscription can be pruned in place
        if (devices.Count == 0)
        {
            return Unit.Value; // no recipient has a push subscription.
        }

        var isGroup = conversation.Type == ConversationType.Group;
        var title = isGroup && !string.IsNullOrWhiteSpace(conversation.Title)
            ? $"{senderName} · {conversation.Title}"
            : senderName;

        var preview = message.SharedMovieTmdbId is > 0
            ? string.IsNullOrWhiteSpace(message.Body) ? $"🎬 Shared \"{message.SharedMovieTitle}\"" : message.Body
            : message.Body;
        preview = Truncate(preview);

        var payload = new PushPayload(title, preview, $"/chat/{message.ConversationId}", $"chat-{message.ConversationId}");

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
                LogChatPushDeviceErrored(logger, exception, device.Id);
                continue;
            }

            if (result == PushSendResult.Gone)
            {
                db.Devices.Remove(device);
                pruned = true;
                LogChatDevicePruned(logger, device.Id);
            }
        }

        if (pruned)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return Unit.Value;
    }

    private static string Truncate(string value) =>
        value.Length <= PreviewMaxLength ? value : value[..PreviewMaxLength];

    [LoggerMessage(
        EventId = 6240,
        Level = LogLevel.Information,
        Message = "Pruned dead Web Push device {DeviceId} during a chat-message push (service returned Gone).")]
    private static partial void LogChatDevicePruned(ILogger logger, Guid deviceId);

    [LoggerMessage(
        EventId = 6241,
        Level = LogLevel.Warning,
        Message = "Chat Web Push to device {DeviceId} threw an unclassified error; the device was skipped and the batch continued.")]
    private static partial void LogChatPushDeviceErrored(ILogger logger, Exception exception, Guid deviceId);
}
