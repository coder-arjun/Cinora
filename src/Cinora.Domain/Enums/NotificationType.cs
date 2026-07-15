namespace Cinora.Domain.Enums;

/// <summary>The kind of event a <see cref="Entities.Notification"/> informs the recipient about.</summary>
public enum NotificationType
{
    /// <summary>Another user sent the recipient a friend request.</summary>
    FriendRequest = 0,

    /// <summary>Another user accepted the recipient's friend request.</summary>
    FriendAccepted = 1,

    /// <summary>Another user liked one of the recipient's reviews.</summary>
    ReviewLiked = 2,

    /// <summary>Another user commented on one of the recipient's reviews.</summary>
    CommentAdded = 3,
}
