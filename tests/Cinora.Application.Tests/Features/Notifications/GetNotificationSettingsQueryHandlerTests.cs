using Cinora.Application.Common.Exceptions;
using Cinora.Application.Features.Notifications;
using Cinora.Application.Tests.Common.Fakes;
using Cinora.Domain.Entities;
using Cinora.Domain.ValueObjects;

namespace Cinora.Application.Tests.Features.Notifications;

/// <summary>
/// Milestone 6.3 unit tests for <see cref="GetNotificationSettingsQueryHandler"/>: it projects the CURRENT user's
/// owned preference columns (owner-resolved via <see cref="StubCurrentUser"/>, no id bound) and defensively 404s a
/// missing row.
/// </summary>
public sealed class GetNotificationSettingsQueryHandlerTests
{
    [Fact]
    public async Task Handle_returns_the_current_users_preferences()
    {
        var userId = Guid.NewGuid();
        using var store = new ProfilesSqliteStore();

        await using (var seed = store.CreateContext())
        {
            var user = User.Create(userId, "Ada");
            user.UpdateNotificationPreferences(new NotificationPreferences(
                PushFriendRequests: true, PushFriendAccepted: false, PushReviewLikes: true, PushComments: false));
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
        }

        await using var db = store.CreateContext();
        var handler = new GetNotificationSettingsQueryHandler(db, new StubCurrentUser(userId));

        var vm = await handler.Handle(new GetNotificationSettingsQuery(), CancellationToken.None);

        Assert.True(vm.PushFriendRequests);
        Assert.False(vm.PushFriendAccepted);
        Assert.True(vm.PushReviewLikes);
        Assert.False(vm.PushComments);
    }

    [Fact]
    public async Task Handle_throws_not_found_when_the_user_row_is_missing()
    {
        using var store = new ProfilesSqliteStore();
        await using var db = store.CreateContext();
        var handler = new GetNotificationSettingsQueryHandler(db, new StubCurrentUser(Guid.NewGuid()));

        await Assert.ThrowsAsync<NotFoundException>(
            () => handler.Handle(new GetNotificationSettingsQuery(), CancellationToken.None));
    }
}
