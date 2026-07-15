using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Domain.Exceptions;
using Cinora.Domain.ValueObjects;

namespace Cinora.Domain.Tests.Entities;

public class UserTests
{
    [Fact]
    public void Create_preserves_id_and_defaults_to_a_public_profile()
    {
        var id = Guid.NewGuid();

        var user = User.Create(id, "Ada");

        Assert.Equal(id, user.Id);
        Assert.Equal("Ada", user.DisplayName);
        Assert.True(user.IsProfilePublic);
        Assert.Null(user.AvatarFileKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_with_blank_display_name_throws_domain_exception(string displayName)
    {
        Assert.Throws<DomainException>(() =>
        {
            _ = User.Create(Guid.NewGuid(), displayName);
        });
    }

    [Fact]
    public void Create_with_display_name_at_max_length_succeeds()
    {
        var displayName = new string('a', User.DisplayNameMaxLength);

        var user = User.Create(Guid.NewGuid(), displayName);

        Assert.Equal(User.DisplayNameMaxLength, user.DisplayName.Length);
    }

    [Fact]
    public void Rename_changes_the_display_name()
    {
        var user = User.Create(Guid.NewGuid(), "Ada");

        user.Rename("Grace");

        Assert.Equal("Grace", user.DisplayName);
    }

    [Fact]
    public void SetProfileVisibility_and_SetAvatar_update_state()
    {
        var user = User.Create(Guid.NewGuid(), "Ada");

        user.SetProfileVisibility(false);
        user.SetAvatar("avatars/ada.png");

        Assert.False(user.IsProfilePublic);
        Assert.Equal("avatars/ada.png", user.AvatarFileKey);
    }

    [Fact]
    public void Create_defaults_to_all_push_notifications_enabled()
    {
        var user = User.Create(Guid.NewGuid(), "Ada");

        Assert.True(user.Preferences.IsPushEnabled(NotificationType.FriendRequest));
        Assert.True(user.Preferences.IsPushEnabled(NotificationType.FriendAccepted));
        Assert.True(user.Preferences.IsPushEnabled(NotificationType.ReviewLiked));
        Assert.True(user.Preferences.IsPushEnabled(NotificationType.CommentAdded));
    }

    [Fact]
    public void UpdateNotificationPreferences_replaces_the_preferences()
    {
        var user = User.Create(Guid.NewGuid(), "Ada");

        user.UpdateNotificationPreferences(new NotificationPreferences(
            PushFriendRequests: false, PushFriendAccepted: true, PushReviewLikes: false, PushComments: true));

        Assert.False(user.Preferences.PushFriendRequests);
        Assert.True(user.Preferences.PushFriendAccepted);
        Assert.False(user.Preferences.IsPushEnabled(NotificationType.ReviewLiked));
        Assert.True(user.Preferences.IsPushEnabled(NotificationType.CommentAdded));
    }

    [Fact]
    public void UpdateNotificationPreferences_rejects_null()
    {
        var user = User.Create(Guid.NewGuid(), "Ada");

        Assert.Throws<ArgumentNullException>(() => user.UpdateNotificationPreferences(null!));
    }
}
