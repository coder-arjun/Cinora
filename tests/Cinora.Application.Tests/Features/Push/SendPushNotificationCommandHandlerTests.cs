using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Push;
using Cinora.Application.Features.Push;
using Cinora.Application.Tests.Common.Fakes;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinora.Application.Tests.Features.Push;

/// <summary>
/// Milestone 6.2 unit tests for <see cref="SendPushNotificationCommandHandler"/> with a fake
/// <see cref="IPushSender"/> (ADR 0020 §3.4): it reloads the notification via the shared projection, builds the
/// correct <c>data.url</c> per <see cref="NotificationType"/> through <c>NotificationDeepLink</c>, deletes a
/// device on <see cref="PushSendResult.Gone"/>, and logs-and-continues on
/// <see cref="PushSendResult.TransientFailure"/>. Milestone 6.2 applies NO preference gate.
/// </summary>
public sealed class SendPushNotificationCommandHandlerTests
{
    private static SendPushNotificationCommandHandler CreateHandler(PushTestDbContext db, IPushSender sender) =>
        new(db, sender, NullLogger<SendPushNotificationCommandHandler>.Instance);

    [Fact]
    public async Task Handle_review_notification_builds_the_details_deep_link_and_sends_to_the_device()
    {
        const int tmdbId = 700_001;
        var recipient = Guid.NewGuid();
        using var store = new PushSqliteStore();

        var notificationId = await SeedReviewNotificationAsync(
            store, recipient, tmdbId, MediaType.Movie, NotificationType.ReviewLiked, "Bob liked your review");

        var sender = new FakePushSender();
        await using (var db = store.CreateContext())
        {
            await CreateHandler(db, sender).Handle(new SendPushNotificationCommand(notificationId), CancellationToken.None);
        }

        var sent = Assert.Single(sender.Sent);
        Assert.Equal("Cinora", sent.Payload.Title);
        Assert.Equal("Bob liked your review", sent.Payload.Body);
        Assert.Equal($"/discover/title/movie/{tmdbId}", sent.Payload.Url);
        Assert.Equal("ReviewLiked", sent.Payload.Tag);
        Assert.Equal("https://push.example/1", sent.Subscription.Endpoint);
    }

    [Fact]
    public async Task Handle_series_comment_notification_uses_the_series_details_deep_link()
    {
        const int tmdbId = 700_002;
        var recipient = Guid.NewGuid();
        using var store = new PushSqliteStore();

        var notificationId = await SeedReviewNotificationAsync(
            store, recipient, tmdbId, MediaType.Series, NotificationType.CommentAdded, "Bob commented");

        var sender = new FakePushSender();
        await using (var db = store.CreateContext())
        {
            await CreateHandler(db, sender).Handle(new SendPushNotificationCommand(notificationId), CancellationToken.None);
        }

        Assert.Equal($"/discover/title/series/{tmdbId}", Assert.Single(sender.Sent).Payload.Url);
    }

    [Fact]
    public async Task Handle_friend_request_notification_deep_links_to_friends()
    {
        var recipient = Guid.NewGuid();
        using var store = new PushSqliteStore();

        var notificationId = await SeedSimpleNotificationAsync(
            store, recipient, NotificationType.FriendRequest, actorUserId: Guid.NewGuid(), targetId: Guid.NewGuid());

        var sender = new FakePushSender();
        await using (var db = store.CreateContext())
        {
            await CreateHandler(db, sender).Handle(new SendPushNotificationCommand(notificationId), CancellationToken.None);
        }

        Assert.Equal("/friends", Assert.Single(sender.Sent).Payload.Url);
    }

    [Fact]
    public async Task Handle_friend_accepted_notification_deep_links_to_the_actor_profile()
    {
        var recipient = Guid.NewGuid();
        var actor = Guid.NewGuid();
        using var store = new PushSqliteStore();

        var notificationId = await SeedSimpleNotificationAsync(
            store, recipient, NotificationType.FriendAccepted, actorUserId: actor, targetId: Guid.NewGuid());

        var sender = new FakePushSender();
        await using (var db = store.CreateContext())
        {
            await CreateHandler(db, sender).Handle(new SendPushNotificationCommand(notificationId), CancellationToken.None);
        }

        Assert.Equal($"/users/{actor}", Assert.Single(sender.Sent).Payload.Url);
    }

    [Fact]
    public async Task Handle_gone_result_deletes_the_dead_device()
    {
        var recipient = Guid.NewGuid();
        using var store = new PushSqliteStore();

        var notificationId = await SeedSimpleNotificationAsync(
            store, recipient, NotificationType.FriendRequest, actorUserId: Guid.NewGuid(), targetId: Guid.NewGuid());

        var sender = new FakePushSender { Result = PushSendResult.Gone };
        await using (var db = store.CreateContext())
        {
            await CreateHandler(db, sender).Handle(new SendPushNotificationCommand(notificationId), CancellationToken.None);
        }

        await using var verify = store.CreateContext();
        Assert.Equal(0, await verify.Devices.AsNoTracking().CountAsync(d => d.UserId == recipient));
    }

    [Fact]
    public async Task Handle_transient_failure_keeps_the_device_and_does_not_throw()
    {
        var recipient = Guid.NewGuid();
        using var store = new PushSqliteStore();

        var notificationId = await SeedSimpleNotificationAsync(
            store, recipient, NotificationType.FriendRequest, actorUserId: Guid.NewGuid(), targetId: Guid.NewGuid());

        var sender = new FakePushSender { Result = PushSendResult.TransientFailure };
        await using (var db = store.CreateContext())
        {
            await CreateHandler(db, sender).Handle(new SendPushNotificationCommand(notificationId), CancellationToken.None);
        }

        await using var verify = store.CreateContext();
        Assert.Equal(1, await verify.Devices.AsNoTracking().CountAsync(d => d.UserId == recipient));
    }

    [Fact]
    public async Task Handle_missing_notification_is_a_noop()
    {
        using var store = new PushSqliteStore();
        var sender = new FakePushSender();

        await using var db = store.CreateContext();
        await CreateHandler(db, sender).Handle(new SendPushNotificationCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.Empty(sender.Sent);
    }

    // ---- Milestone 6.3: the per-type preference gate (push only; in-app row always persists) ---------------

    [Fact]
    public async Task Handle_muted_type_is_not_pushed_but_the_inbox_row_still_persists()
    {
        var recipient = Guid.NewGuid();
        using var store = new PushSqliteStore();

        // Seed the recipient with ReviewLikes push MUTED, plus a ReviewLiked notification + a device.
        var notificationId = await SeedGatedReviewNotificationAsync(
            store, recipient, tmdbId: 700_010, NotificationType.ReviewLiked,
            preferences: new NotificationPreferences(
                PushFriendRequests: true, PushFriendAccepted: true, PushReviewLikes: false, PushComments: true));

        var sender = new FakePushSender();
        await using (var db = store.CreateContext())
        {
            await CreateHandler(db, sender).Handle(new SendPushNotificationCommand(notificationId), CancellationToken.None);
        }

        Assert.Empty(sender.Sent); // muted → NO push sent

        // The in-app inbox row is unconditional — the dispatcher never touched it.
        await using var verify = store.CreateContext();
        Assert.Equal(1, await verify.Notifications.AsNoTracking().CountAsync(n => n.Id == notificationId));
        Assert.Equal(1, await verify.Devices.AsNoTracking().CountAsync(d => d.UserId == recipient)); // not pruned
    }

    [Fact]
    public async Task Handle_enabled_type_is_pushed_when_the_preference_is_on()
    {
        var recipient = Guid.NewGuid();
        using var store = new PushSqliteStore();

        var notificationId = await SeedGatedReviewNotificationAsync(
            store, recipient, tmdbId: 700_011, NotificationType.ReviewLiked,
            preferences: NotificationPreferences.Default); // all enabled

        var sender = new FakePushSender();
        await using (var db = store.CreateContext())
        {
            await CreateHandler(db, sender).Handle(new SendPushNotificationCommand(notificationId), CancellationToken.None);
        }

        Assert.Single(sender.Sent); // enabled → pushed
    }

    // ---- Milestone 6.2 folded code Low: a malformed device must not abort the batch or skip the Gone-prune ----

    [Fact]
    public async Task Handle_one_device_that_throws_does_not_abort_the_batch_or_skip_the_gone_prune()
    {
        var recipient = Guid.NewGuid();
        const string throwingEndpoint = "https://push.example/throws";
        const string goneEndpoint = "https://push.example/gone";
        using var store = new PushSqliteStore();

        await using (var seed = store.CreateContext())
        {
            var notification = Notification.Create(
                recipient, NotificationType.FriendRequest, "A notification.",
                actorUserId: Guid.NewGuid(), targetId: Guid.NewGuid());
            seed.Notifications.Add(notification);
            seed.Devices.Add(Device.Register(recipient, throwingEndpoint, "p256dh", "auth"));
            seed.Devices.Add(Device.Register(recipient, goneEndpoint, "p256dh", "auth"));
            await seed.SaveChangesAsync();

            var sender = new FakePushSender { Result = PushSendResult.Gone };
            sender.ThrowForEndpoints.Add(throwingEndpoint);

            await using var db = store.CreateContext();
            // Must NOT throw despite the malformed device.
            await CreateHandler(db, sender).Handle(new SendPushNotificationCommand(notification.Id), CancellationToken.None);
        }

        await using var verify = store.CreateContext();
        var remaining = await verify.Devices.AsNoTracking().Where(d => d.UserId == recipient).ToListAsync();

        // The Gone device was pruned (so the deferred prune SaveChanges DID run) and the throwing device was
        // skipped-and-kept — the batch was neither aborted nor did it skip the prune.
        var only = Assert.Single(remaining);
        Assert.Equal(throwingEndpoint, only.Endpoint);
    }

    // Seeds a Movie + a Review authored by the recipient + a review-type Notification targeting that review, plus
    // one Device for the recipient. Returns the notification id.
    private static async Task<Guid> SeedReviewNotificationAsync(
        PushSqliteStore store, Guid recipient, int tmdbId, MediaType media, NotificationType type, string message)
    {
        await using var seed = store.CreateContext();

        var movie = Movie.FromTmdb(tmdbId, media, $"Title {tmdbId}", null, null, null, null);
        seed.Movies.Add(movie);
        var review = Review.Create(recipient, movie.Id, Rating.From(9), "A review body.");
        seed.Reviews.Add(review);
        var notification = Notification.Create(recipient, type, message, actorUserId: Guid.NewGuid(), targetId: review.Id);
        seed.Notifications.Add(notification);
        seed.Devices.Add(Device.Register(recipient, "https://push.example/1", "p256dh", "auth"));

        await seed.SaveChangesAsync();
        return notification.Id;
    }

    // Seeds a recipient USER (with the given push preferences) + a Movie + a Review by that user + a review-type
    // Notification targeting it + one Device. Used by the 6.3 preference-gate tests, where the recipient's
    // preferences must exist to be consulted. Returns the notification id.
    private static async Task<Guid> SeedGatedReviewNotificationAsync(
        PushSqliteStore store, Guid recipient, int tmdbId, NotificationType type, NotificationPreferences preferences)
    {
        await using var seed = store.CreateContext();

        var user = User.Create(recipient, "Rey Recipient");
        user.UpdateNotificationPreferences(preferences);
        seed.Users.Add(user);

        var movie = Movie.FromTmdb(tmdbId, MediaType.Movie, $"Title {tmdbId}", null, null, null, null);
        seed.Movies.Add(movie);
        var review = Review.Create(recipient, movie.Id, Rating.From(9), "A review body.");
        seed.Reviews.Add(review);
        var notification = Notification.Create(recipient, type, "Bob liked your review", actorUserId: Guid.NewGuid(), targetId: review.Id);
        seed.Notifications.Add(notification);
        seed.Devices.Add(Device.Register(recipient, "https://push.example/1", "p256dh", "auth"));

        await seed.SaveChangesAsync();
        return notification.Id;
    }

    // Seeds a non-review Notification (friend event) plus one Device for the recipient. Returns the notification id.
    private static async Task<Guid> SeedSimpleNotificationAsync(
        PushSqliteStore store, Guid recipient, NotificationType type, Guid? actorUserId, Guid targetId)
    {
        await using var seed = store.CreateContext();

        var notification = Notification.Create(recipient, type, "A notification.", actorUserId, targetId);
        seed.Notifications.Add(notification);
        seed.Devices.Add(Device.Register(recipient, "https://push.example/1", "p256dh", "auth"));

        await seed.SaveChangesAsync();
        return notification.Id;
    }
}
