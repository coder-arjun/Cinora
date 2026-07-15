using Cinora.Application.Features.Notifications;
using Cinora.Application.Tests.Common.Fakes;
using Cinora.Domain.Entities;
using Cinora.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Tests.Features.Notifications;

/// <summary>
/// Milestone 6.3 unit tests for <see cref="UpdateNotificationPreferencesCommandHandler"/> against an in-process
/// SQLite <see cref="ProfilesTestDbContext"/> (which maps the owned <see cref="NotificationPreferences"/>). They
/// prove the update lands on the current user and — the ownership contract (ADR 0009) — that a second user's
/// preferences are never touched, because the actor is server-resolved and NO id is bound from the request.
/// </summary>
public sealed class UpdateNotificationPreferencesCommandHandlerTests
{
    [Fact]
    public async Task Handle_updates_the_current_users_preferences()
    {
        var userId = Guid.NewGuid();
        using var store = new ProfilesSqliteStore();

        await using (var seed = store.CreateContext())
        {
            seed.Users.Add(User.Create(userId, "Ada"));
            await seed.SaveChangesAsync();
        }

        await using (var db = store.CreateContext())
        {
            var handler = new UpdateNotificationPreferencesCommandHandler(db, new StubCurrentUser(userId));
            await handler.Handle(
                new UpdateNotificationPreferencesCommand(
                    PushFriendRequests: false, PushFriendAccepted: true, PushReviewLikes: false, PushComments: true),
                CancellationToken.None);
        }

        await using var verify = store.CreateContext();
        var prefs = await verify.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.Preferences)
            .SingleAsync();

        Assert.False(prefs.PushFriendRequests);
        Assert.True(prefs.PushFriendAccepted);
        Assert.False(prefs.PushReviewLikes);
        Assert.True(prefs.PushComments);
    }

    [Fact]
    public async Task Handle_never_touches_another_users_preferences()
    {
        var currentUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        using var store = new ProfilesSqliteStore();

        await using (var seed = store.CreateContext())
        {
            seed.Users.Add(User.Create(currentUserId, "Ada"));
            seed.Users.Add(User.Create(otherUserId, "Grace")); // defaults to all-enabled
            await seed.SaveChangesAsync();
        }

        await using (var db = store.CreateContext())
        {
            // The command binds NO user id; the acting user is the server-resolved current user only.
            var handler = new UpdateNotificationPreferencesCommandHandler(db, new StubCurrentUser(currentUserId));
            await handler.Handle(
                new UpdateNotificationPreferencesCommand(
                    PushFriendRequests: false, PushFriendAccepted: false, PushReviewLikes: false, PushComments: false),
                CancellationToken.None);
        }

        await using var verify = store.CreateContext();

        var mine = await verify.Users.AsNoTracking()
            .Where(u => u.Id == currentUserId).Select(u => u.Preferences).SingleAsync();
        Assert.False(mine.PushFriendRequests);
        Assert.False(mine.PushComments);

        // The OTHER user's preferences are exactly as seeded (all enabled) — no cross-user edit.
        var theirs = await verify.Users.AsNoTracking()
            .Where(u => u.Id == otherUserId).Select(u => u.Preferences).SingleAsync();
        Assert.True(theirs.PushFriendRequests);
        Assert.True(theirs.PushFriendAccepted);
        Assert.True(theirs.PushReviewLikes);
        Assert.True(theirs.PushComments);
    }
}
