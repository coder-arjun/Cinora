using Cinora.Domain.Entities;
using Cinora.Domain.Enums;

namespace Cinora.Domain.Tests.Entities;

public class NotificationTests
{
    [Fact]
    public void Create_produces_an_unread_notification()
    {
        var notification = Notification.Create(
            Guid.NewGuid(),
            NotificationType.FriendRequest,
            "You have a new friend request.",
            Guid.NewGuid(),
            null);

        Assert.False(notification.IsRead);
    }

    [Fact]
    public void MarkRead_marks_the_notification_as_read()
    {
        var notification = Notification.Create(
            Guid.NewGuid(),
            NotificationType.ReviewLiked,
            "Someone liked your review.",
            Guid.NewGuid(),
            Guid.NewGuid());

        notification.MarkRead();

        Assert.True(notification.IsRead);
    }
}
