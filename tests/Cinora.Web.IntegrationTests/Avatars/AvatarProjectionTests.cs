using Cinora.Application.Common.Interfaces;
using Cinora.Application.Features.Comments;
using Cinora.Application.Features.Feed;
using Cinora.Application.Features.Friends;
using Cinora.Application.Features.Notifications;
using Cinora.Application.Features.Profiles;
using Cinora.Application.Features.Reviews;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Domain.ValueObjects;
using Cinora.Infrastructure.Identity;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Web.IntegrationTests.Avatars;

/// <summary>
/// Milestone 4.2 Step B tests: prove the user's <c>AvatarFileKey</c> flows through the EXISTING shared read-model
/// projections into the display view models the <c>_Avatar</c> seam renders — a user WITH an avatar surfaces a
/// populated key, a user WITHOUT one surfaces <c>null</c> (the seam then renders the monogram fallback). Covers
/// the review, feed, comment, notification, friend, pending-request, and profile VMs. They exercise the real
/// shared projections against the migrated
/// <c>CinoraTest</c> LocalDB by dispatching the actual query handlers with the resolved
/// <see cref="IAppDbContext"/> and a fixed <see cref="ICurrentUser"/>, so the assertion is on the VM the view
/// receives — independent of markup. TMDB ids sit outside other suites' seeded sets to keep the shared database
/// isolated.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class AvatarProjectionTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public AvatarProjectionTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // A review by an author WITH an avatar surfaces the key on ReviewVm; a review by an author WITHOUT one → null.
    [Fact]
    public async Task Review_vm_carries_the_author_avatar_key_or_null()
    {
        var withAvatar = await RegisterServerUserAsync("Rev Author WithAvatar");
        var noAvatar = await RegisterServerUserAsync("Rev Author NoAvatar");
        var key = await SetAvatarAsync(withAvatar);

        var movieId = await SeedMovieAsync(970_001);
        var reviewWith = await SeedReviewAsync(withAvatar, movieId, "review-with-avatar");
        var movieId2 = await SeedMovieAsync(970_002);
        var reviewWithout = await SeedReviewAsync(noAvatar, movieId2, "review-without-avatar");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var handler = new GetReviewCardQueryHandler(db, new StubCurrentUser(withAvatar));

        var vmWith = await handler.Handle(new GetReviewCardQuery(reviewWith), CancellationToken.None);
        var vmWithout = await handler.Handle(new GetReviewCardQuery(reviewWithout), CancellationToken.None);

        Assert.NotNull(vmWith);
        Assert.Equal(key, vmWith!.AuthorAvatarFileKey);
        Assert.NotNull(vmWithout);
        Assert.Null(vmWithout!.AuthorAvatarFileKey);
    }

    // A friend's feed item carries the author avatar key (or null) on FeedItemVm.
    [Fact]
    public async Task Feed_item_vm_carries_the_author_avatar_key_or_null()
    {
        var viewer = await RegisterServerUserAsync("Feed Viewer");
        var friendWith = await RegisterServerUserAsync("Feed Friend WithAvatar");
        var friendWithout = await RegisterServerUserAsync("Feed Friend NoAvatar");
        var key = await SetAvatarAsync(friendWith);

        await MakeAcceptedFriendsAsync(viewer, friendWith);
        await MakeAcceptedFriendsAsync(viewer, friendWithout);

        var movieA = await SeedMovieAsync(970_010);
        var movieB = await SeedMovieAsync(970_011);
        await SeedReviewAsync(friendWith, movieA, "feed-with-avatar");
        await SeedReviewAsync(friendWithout, movieB, "feed-without-avatar");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var handler = new GetActivityFeedQueryHandler(db, new StubCurrentUser(viewer));

        var feed = await handler.Handle(new GetActivityFeedQuery(Cursor: null), CancellationToken.None);

        var itemWith = feed.Items.Single(item => item.AuthorUserId == friendWith);
        var itemWithout = feed.Items.Single(item => item.AuthorUserId == friendWithout);
        Assert.Equal(key, itemWith.AuthorAvatarFileKey);
        Assert.Null(itemWithout.AuthorAvatarFileKey);
    }

    // A comment by an author WITH an avatar surfaces the key on CommentVm; one WITHOUT → null.
    [Fact]
    public async Task Comment_vm_carries_the_author_avatar_key_or_null()
    {
        var reviewAuthor = await RegisterServerUserAsync("Cmt Review Author");
        var commenterWith = await RegisterServerUserAsync("Cmt WithAvatar");
        var commenterWithout = await RegisterServerUserAsync("Cmt NoAvatar");
        var key = await SetAvatarAsync(commenterWith);

        var movieId = await SeedMovieAsync(970_020);
        var reviewId = await SeedReviewAsync(reviewAuthor, movieId, "comment-host-review");
        await SeedCommentAsync(reviewId, commenterWith, "comment-with-avatar");
        await SeedCommentAsync(reviewId, commenterWithout, "comment-without-avatar");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var handler = new GetReviewCommentsQueryHandler(db, new StubCurrentUser(reviewAuthor));

        var thread = await handler.Handle(
            new GetReviewCommentsQuery(reviewId, Cursor: null), CancellationToken.None);

        var cmtWith = thread.Items.Single(item => item.AuthorUserId == commenterWith);
        var cmtWithout = thread.Items.Single(item => item.AuthorUserId == commenterWithout);
        Assert.Equal(key, cmtWith.AuthorAvatarFileKey);
        Assert.Null(cmtWithout.AuthorAvatarFileKey);
    }

    // A notification's actor avatar key flows onto NotificationVm (or null when the actor has none).
    [Fact]
    public async Task Notification_vm_carries_the_actor_avatar_key_or_null()
    {
        var recipient = await RegisterServerUserAsync("Notif Recipient");
        var actorWith = await RegisterServerUserAsync("Notif Actor WithAvatar");
        var actorWithout = await RegisterServerUserAsync("Notif Actor NoAvatar");
        var key = await SetAvatarAsync(actorWith);

        await SeedNotificationAsync(recipient, actorWith, "notif-with-avatar");
        await SeedNotificationAsync(recipient, actorWithout, "notif-without-avatar");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var handler = new GetNotificationsQueryHandler(db, new StubCurrentUser(recipient));

        var inbox = await handler.Handle(new GetNotificationsQuery(Cursor: null), CancellationToken.None);

        var notifWith = inbox.Items.Single(item => item.ActorUserId == actorWith);
        var notifWithout = inbox.Items.Single(item => item.ActorUserId == actorWithout);
        Assert.Equal(key, notifWith.ActorAvatarFileKey);
        Assert.Null(notifWithout.ActorAvatarFileKey);
    }

    // A friend WITH an avatar surfaces the key on FriendVm; a friend WITHOUT one → null.
    [Fact]
    public async Task Friend_vm_carries_the_friend_avatar_key_or_null()
    {
        var viewer = await RegisterServerUserAsync("Friend List Viewer");
        var friendWith = await RegisterServerUserAsync("Friend WithAvatar");
        var friendWithout = await RegisterServerUserAsync("Friend NoAvatar");
        var key = await SetAvatarAsync(friendWith);

        await MakeAcceptedFriendsAsync(viewer, friendWith);
        await MakeAcceptedFriendsAsync(viewer, friendWithout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var handler = new GetFriendsQueryHandler(db, new StubCurrentUser(viewer));

        var result = await handler.Handle(new GetFriendsQuery(), CancellationToken.None);

        var friendVmWith = result.Friends.Single(friend => friend.UserId == friendWith);
        var friendVmWithout = result.Friends.Single(friend => friend.UserId == friendWithout);
        Assert.Equal(key, friendVmWith.AvatarFileKey);
        Assert.Null(friendVmWithout.AvatarFileKey);
    }

    // An incoming request's OTHER party WITH an avatar surfaces the key on PendingRequestVm; WITHOUT one → null.
    [Fact]
    public async Task Pending_request_vm_carries_the_other_avatar_key_or_null()
    {
        var viewer = await RegisterServerUserAsync("Pending Viewer");
        var requesterWith = await RegisterServerUserAsync("Pending Requester WithAvatar");
        var requesterWithout = await RegisterServerUserAsync("Pending Requester NoAvatar");
        var key = await SetAvatarAsync(requesterWith);

        // Requests addressed TO the viewer → incoming, so their "other" party is the requester.
        await MakePendingRequestAsync(requesterWith, viewer);
        await MakePendingRequestAsync(requesterWithout, viewer);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var handler = new GetPendingRequestsQueryHandler(db, new StubCurrentUser(viewer));

        var result = await handler.Handle(new GetPendingRequestsQuery(), CancellationToken.None);

        var reqWith = result.Incoming.Single(request => request.OtherUserId == requesterWith);
        var reqWithout = result.Incoming.Single(request => request.OtherUserId == requesterWithout);
        Assert.Equal(key, reqWith.OtherAvatarFileKey);
        Assert.Null(reqWithout.OtherAvatarFileKey);
    }

    // A profile owner WITH an avatar surfaces the key on ProfileVm; an owner WITHOUT one → null. The avatar key is
    // revealed regardless of privacy/relationship (name + avatar are always shown), so a non-friend viewer is fine.
    [Fact]
    public async Task Profile_vm_carries_the_owner_avatar_key_or_null()
    {
        var viewer = await RegisterServerUserAsync("Profile Viewer");
        var ownerWith = await RegisterServerUserAsync("Profile Owner WithAvatar");
        var ownerWithout = await RegisterServerUserAsync("Profile Owner NoAvatar");
        var key = await SetAvatarAsync(ownerWith);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var handler = new GetProfileQueryHandler(db, new StubCurrentUser(viewer));

        var profileWith = await handler.Handle(new GetProfileQuery(ownerWith), CancellationToken.None);
        var profileWithout = await handler.Handle(new GetProfileQuery(ownerWithout), CancellationToken.None);

        Assert.Equal(key, profileWith.AvatarFileKey);
        Assert.Null(profileWithout.AvatarFileKey);
    }

    // ---- seed helpers -------------------------------------------------------------------------------------

    // Creates a user through the atomic registration seam (identity + domain rows share one PK) without needing an
    // authenticated client — this suite asserts VMs directly and never posts as these users.
    private async Task<Guid> RegisterServerUserAsync(string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var registration = scope.ServiceProvider.GetRequiredService<IUserRegistrationService>();
        var result = await registration.RegisterAsync(
            $"avatar-{Guid.NewGuid():N}@cinora.test", "Test1234!", $"{displayName} {Guid.NewGuid():N}", CancellationToken.None);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Seed registration for '{displayName}' failed.");
        }

        return result.UserId;
    }

    // Sets a well-formed avatar key on the domain User row and returns it (matches the LocalFileStorage key shape).
    private async Task<string> SetAvatarAsync(Guid userId)
    {
        var key = $"avatars/{Guid.NewGuid():N}.jpg";
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var user = await db.Set<User>().SingleAsync(u => u.Id == userId);
        user.SetAvatar(key);
        await db.SaveChangesAsync();
        return key;
    }

    private async Task MakeAcceptedFriendsAsync(Guid a, Guid b)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var friend = Friend.Request(a, b);
        friend.Accept();
        db.Friends.Add(friend);
        await db.SaveChangesAsync();
    }

    // Seeds a still-pending (not accepted) friend request from requester → addressee.
    private async Task MakePendingRequestAsync(Guid requesterId, Guid addresseeId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        db.Friends.Add(Friend.Request(requesterId, addresseeId));
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedMovieAsync(int tmdbId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var movie = Movie.FromTmdb(tmdbId, MediaType.Movie, $"Avatar Title {tmdbId}", null, null, null, null);
        db.Movies.Add(movie);
        await db.SaveChangesAsync();
        return movie.Id;
    }

    private async Task<Guid> SeedReviewAsync(Guid authorId, Guid movieId, string body)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var review = Review.Create(authorId, movieId, Rating.From(8), body);
        db.Reviews.Add(review);
        await db.SaveChangesAsync();
        return review.Id;
    }

    private async Task SeedCommentAsync(Guid reviewId, Guid authorId, string body)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        db.Comments.Add(Comment.Create(reviewId, authorId, body));
        await db.SaveChangesAsync();
    }

    private async Task SeedNotificationAsync(Guid recipientId, Guid actorId, string message)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        db.Notifications.Add(
            Notification.Create(recipientId, NotificationType.FriendRequest, message, actorId, targetId: null));
        await db.SaveChangesAsync();
    }

    // A fixed, always-authenticated ICurrentUser — the viewer the handlers project VMs for (no id is bound from a
    // request; hand-rolled double, no mocking package, consistent with the rest of the suite).
    private sealed class StubCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;

        public bool IsAuthenticated => true;

        public Guid GetRequiredUserId() => userId;
    }
}
