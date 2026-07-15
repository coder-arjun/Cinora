using Cinora.Domain.Enums;
using Cinora.Domain.ValueObjects;

namespace Cinora.Domain.Tests.ValueObjects;

public class NotificationPreferencesTests
{
    [Fact]
    public void Default_enables_push_for_every_type()
    {
        var preferences = NotificationPreferences.Default;

        Assert.True(preferences.PushFriendRequests);
        Assert.True(preferences.PushFriendAccepted);
        Assert.True(preferences.PushReviewLikes);
        Assert.True(preferences.PushComments);
        Assert.True(preferences.IsPushEnabled(NotificationType.FriendRequest));
        Assert.True(preferences.IsPushEnabled(NotificationType.FriendAccepted));
        Assert.True(preferences.IsPushEnabled(NotificationType.ReviewLiked));
        Assert.True(preferences.IsPushEnabled(NotificationType.CommentAdded));
    }

    [Theory]
    [InlineData(NotificationType.FriendRequest)]
    [InlineData(NotificationType.FriendAccepted)]
    [InlineData(NotificationType.ReviewLiked)]
    [InlineData(NotificationType.CommentAdded)]
    public void IsPushEnabled_maps_each_type_to_its_own_flag(NotificationType type)
    {
        // Mute ONLY the flag under test; IsPushEnabled(type) must be false while every other type stays enabled.
        var muted = NotificationPreferences.Default.With(type, enabled: false);

        Assert.False(muted.IsPushEnabled(type));
        foreach (var other in Enum.GetValues<NotificationType>())
        {
            if (other != type)
            {
                Assert.True(muted.IsPushEnabled(other));
            }
        }
    }

    [Fact]
    public void With_returns_a_new_value_changing_only_the_targeted_flag_and_never_mutates_the_original()
    {
        var original = NotificationPreferences.Default;

        var updated = original.With(NotificationType.ReviewLiked, enabled: false);

        // The new value flipped only ReviewLikes.
        Assert.False(updated.PushReviewLikes);
        Assert.True(updated.PushFriendRequests);
        Assert.True(updated.PushFriendAccepted);
        Assert.True(updated.PushComments);

        // The original is untouched (immutability).
        Assert.True(original.PushReviewLikes);
        Assert.NotSame(original, updated);
    }

    [Fact]
    public void Constructor_sets_each_flag_and_records_are_value_equal()
    {
        var a = new NotificationPreferences(
            PushFriendRequests: true, PushFriendAccepted: false, PushReviewLikes: true, PushComments: false);
        var b = new NotificationPreferences(
            PushFriendRequests: true, PushFriendAccepted: false, PushReviewLikes: true, PushComments: false);

        Assert.False(a.PushFriendAccepted);
        Assert.False(a.PushComments);
        Assert.Equal(a, b); // value equality
    }
}
