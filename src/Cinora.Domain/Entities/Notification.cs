using Cinora.Domain.Common;
using Cinora.Domain.Enums;

namespace Cinora.Domain.Entities;

/// <summary>
/// An in-app notification delivered to a recipient about a social event (a friend request, a like,
/// a comment, and so on). <see cref="TargetId"/> optionally deep-links to the related entity.
/// </summary>
public sealed class Notification
{
    /// <summary>The maximum allowed length of a notification <see cref="Message"/>.</summary>
    public const int MessageMaxLength = 500;

    private Notification()
    {
    }

    /// <summary>The unique identifier of the notification.</summary>
    public Guid Id { get; private set; }

    /// <summary>The identifier of the user who receives the notification.</summary>
    public Guid RecipientUserId { get; private set; }

    /// <summary>The kind of event this notification represents.</summary>
    public NotificationType Type { get; private set; }

    /// <summary>The identifier of the user who triggered the event, or <c>null</c> for system events.</summary>
    public Guid? ActorUserId { get; private set; }

    /// <summary>The human-readable notification text.</summary>
    public string Message { get; private set; } = null!;

    /// <summary>The identifier of the related entity to deep-link to (for example a review), or <c>null</c>.</summary>
    public Guid? TargetId { get; private set; }

    /// <summary>Whether the recipient has read the notification.</summary>
    public bool IsRead { get; private set; }

    /// <summary>The UTC instant the notification was created.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Creates a new unread notification.</summary>
    /// <param name="recipientUserId">The recipient's identifier.</param>
    /// <param name="type">The kind of event.</param>
    /// <param name="message">The notification text; required, max <see cref="MessageMaxLength"/> characters.</param>
    /// <param name="actorUserId">The triggering user's identifier, or <c>null</c> for system events.</param>
    /// <param name="targetId">The related entity's identifier to deep-link to, or <c>null</c>.</param>
    /// <returns>A new, unread <see cref="Notification"/>.</returns>
    public static Notification Create(
        Guid recipientUserId,
        NotificationType type,
        string message,
        Guid? actorUserId,
        Guid? targetId) =>
        new()
        {
            Id = Guid.NewGuid(),
            RecipientUserId = recipientUserId,
            Type = type,
            Message = Guard.Required(message, MessageMaxLength, nameof(message)),
            ActorUserId = actorUserId,
            TargetId = targetId,
            CreatedAtUtc = DateTime.UtcNow,
        };

    /// <summary>Marks the notification as read.</summary>
    public void MarkRead() => IsRead = true;
}
