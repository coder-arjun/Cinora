using Cinora.Application.Features.Reviews;
using Cinora.Application.Tests.Common.Fakes;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinora.Application.Tests.Features.Push;

/// <summary>
/// Milestone 6.2 tests that the ADR-0011 post-commit dispatch seam, extended in 6.2 (ADR 0020 §3.4), enqueues the
/// Web Push fan-out AFTER the realtime push — driven through a real producing handler
/// (<see cref="LikeReviewCommandHandler"/>) so the "thread <c>IPushDispatch</c> through the 4 handlers" wiring is
/// exercised end-to-end. A genuinely new like that co-persists a notification enqueues exactly one push (keyed by
/// the committed notification id); a self-like (no notification) enqueues nothing.
/// </summary>
public sealed class PushDispatchSeamTests
{
    private const int TmdbId = 710_001;

    [Fact]
    public async Task Genuine_like_enqueues_one_push_for_the_committed_notification()
    {
        var author = Guid.NewGuid();
        var actor = Guid.NewGuid();
        using var store = new PushSqliteStore();
        var reviewId = await SeedReviewAsync(store, author, actorDisplayName: ("Bob Liker", actor));

        var dispatch = new FakePushDispatch();
        await using (var db = store.CreateContext())
        {
            var handler = new LikeReviewCommandHandler(
                db, new StubCurrentUser(actor), new FakeRealtimeNotifier(), dispatch,
                NullLogger<LikeReviewCommandHandler>.Instance);
            await handler.Handle(new LikeReviewCommand(reviewId), CancellationToken.None);
        }

        await using var verify = store.CreateContext();
        var notification = await verify.Notifications.AsNoTracking()
            .SingleAsync(n => n.RecipientUserId == author && n.Type == NotificationType.ReviewLiked);

        var enqueued = Assert.Single(dispatch.Enqueued);          // enqueued exactly once, after commit
        Assert.Equal(notification.Id, enqueued);                  // keyed by the committed notification id
    }

    [Fact]
    public async Task Like_still_persists_the_inbox_row_and_enqueues_push_even_when_the_author_muted_that_type()
    {
        // Milestone 6.3 invariant: the producing handler is preference-AGNOSTIC — the in-app inbox row and the push
        // ENQUEUE are unconditional; the per-type mute is enforced later, ONLY in the push dispatcher (§5.2). So an
        // author who muted review-like PUSH still gets the inbox notification AND the fan-out is still enqueued
        // (the dispatcher is where the mute is honoured, not here).
        var author = Guid.NewGuid();
        var actor = Guid.NewGuid();
        using var store = new PushSqliteStore();
        var reviewId = await SeedReviewAsync(
            store, author, actorDisplayName: ("Bob Liker", actor),
            authorPreferences: new NotificationPreferences(
                PushFriendRequests: true, PushFriendAccepted: true, PushReviewLikes: false, PushComments: true));

        var dispatch = new FakePushDispatch();
        await using (var db = store.CreateContext())
        {
            var handler = new LikeReviewCommandHandler(
                db, new StubCurrentUser(actor), new FakeRealtimeNotifier(), dispatch,
                NullLogger<LikeReviewCommandHandler>.Instance);
            await handler.Handle(new LikeReviewCommand(reviewId), CancellationToken.None);
        }

        await using var verify = store.CreateContext();
        // The in-app row persisted regardless of the (push) preference.
        Assert.Equal(1, await verify.Notifications.AsNoTracking()
            .CountAsync(n => n.RecipientUserId == author && n.Type == NotificationType.ReviewLiked));
        // And the fan-out was still enqueued — the mute is the dispatcher's job, not the producing handler's.
        Assert.Single(dispatch.Enqueued);
    }

    [Fact]
    public async Task Self_like_creates_no_notification_and_enqueues_no_push()
    {
        var author = Guid.NewGuid();
        using var store = new PushSqliteStore();
        var reviewId = await SeedReviewAsync(store, author, actorDisplayName: null);

        var dispatch = new FakePushDispatch();
        await using (var db = store.CreateContext())
        {
            var handler = new LikeReviewCommandHandler(
                db, new StubCurrentUser(author), new FakeRealtimeNotifier(), dispatch,
                NullLogger<LikeReviewCommandHandler>.Instance);
            await handler.Handle(new LikeReviewCommand(reviewId), CancellationToken.None);
        }

        Assert.Empty(dispatch.Enqueued);                          // no notification → no push enqueued

        await using var verify = store.CreateContext();
        Assert.Equal(0, await verify.Notifications.AsNoTracking().CountAsync(n => n.RecipientUserId == author));
    }

    // Seeds the review's author (+ a Movie + the Review) and, when a genuine liker is supplied, that actor user
    // (its DisplayName is read by the like handler when composing the notification). An optional author preference
    // set proves the in-app path is preference-agnostic. Returns the review id.
    private static async Task<Guid> SeedReviewAsync(
        PushSqliteStore store, Guid authorId, (string Name, Guid Id)? actorDisplayName,
        NotificationPreferences? authorPreferences = null)
    {
        await using var seed = store.CreateContext();

        var author = User.Create(authorId, "Ada Author");
        if (authorPreferences is not null)
        {
            author.UpdateNotificationPreferences(authorPreferences);
        }

        seed.Users.Add(author);
        if (actorDisplayName is (string name, Guid actorId))
        {
            seed.Users.Add(User.Create(actorId, name));
        }

        var movie = Movie.FromTmdb(TmdbId, MediaType.Movie, "Seed Title", null, null, null, null);
        seed.Movies.Add(movie);
        var review = Review.Create(authorId, movie.Id, Rating.From(8), "Seed review body.");
        seed.Reviews.Add(review);

        await seed.SaveChangesAsync();
        return review.Id;
    }
}
