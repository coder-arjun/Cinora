using Cinora.Application.Features.Notifications;
using Cinora.Domain.Enums;

namespace Cinora.Application.Tests.Features.Push;

/// <summary>
/// Milestone 6.2 unit tests for <see cref="NotificationDeepLink.Resolve"/> — the single shared notification →
/// route grammar the inbox card and the Web Push <c>data.url</c> both use (ADR 0020 §5). They lock parity with
/// the grammar previously inlined in <c>_NotificationItem.cshtml</c> (no <c>#review-{id}</c> fragment).
/// </summary>
public sealed class NotificationDeepLinkTests
{
    [Fact]
    public void ReviewLiked_with_movie_coords_links_to_the_title_details_page()
    {
        var url = NotificationDeepLink.Resolve(NotificationType.ReviewLiked, 550, MediaType.Movie, actorUserId: null);
        Assert.Equal("/discover/title/movie/550", url);
    }

    [Fact]
    public void CommentAdded_with_series_coords_links_to_the_series_details_page()
    {
        var url = NotificationDeepLink.Resolve(NotificationType.CommentAdded, 1399, MediaType.Series, actorUserId: null);
        Assert.Equal("/discover/title/series/1399", url);
    }

    [Fact]
    public void Review_event_without_coords_resolves_to_null()
    {
        var url = NotificationDeepLink.Resolve(NotificationType.ReviewLiked, null, null, actorUserId: null);
        Assert.Null(url);
    }

    [Fact]
    public void FriendRequest_links_to_the_friends_page()
    {
        var url = NotificationDeepLink.Resolve(NotificationType.FriendRequest, null, null, actorUserId: null);
        Assert.Equal("/friends", url);
    }

    [Fact]
    public void FriendAccepted_with_an_actor_links_to_the_actor_profile()
    {
        var actorId = Guid.NewGuid();
        var url = NotificationDeepLink.Resolve(NotificationType.FriendAccepted, null, null, actorId);
        Assert.Equal($"/users/{actorId}", url);
    }

    [Fact]
    public void FriendAccepted_without_an_actor_resolves_to_null()
    {
        var url = NotificationDeepLink.Resolve(NotificationType.FriendAccepted, null, null, actorUserId: null);
        Assert.Null(url);
    }
}
