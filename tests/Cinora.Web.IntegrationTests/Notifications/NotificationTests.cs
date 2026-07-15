using System.Net;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Realtime;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Domain.ValueObjects;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;

namespace Cinora.Web.IntegrationTests.Notifications;

/// <summary>
/// Milestone 3.5 mock-first tests for the Notifications + realtime slice, driven end-to-end through the real
/// MVC + hand-rolled <c>ISender</c> + Identity pipeline (and the self-hosted SignalR hub) against the migrated
/// <c>CinoraTest</c> LocalDB. They prove the §9.5 plan N1–N6: like co-persists a recipient/actor/target-scoped
/// <c>ReviewLiked</c> notification (N1); a THROWING <see cref="IRealtimeNotifier"/> still commits the like +
/// notification and logs a Warning (N2); an anonymous SignalR negotiate is rejected (N3); the inbox returns only
/// the current user's notifications, newest-first keyset with no overlap (N4); marking a notification you don't
/// own is <c>NotFound</c> — no cross-user mutation (N5); and <c>GET /notifications</c> keeps the strict CSP (N6).
/// Reuses <see cref="TestAuthentication"/> (≥2 users); notifications touch no TMDB, so the shared factory is used
/// directly and reviews are seeded through the DbContext.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class NotificationTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public NotificationTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // N1 — A likes B's review → a ReviewLiked Notification with recipient=B, actor=A, target=ReviewId.
    [Fact]
    public async Task N1_Like_co_persists_a_review_liked_notification_to_the_author()
    {
        const int tmdbId = 950_001;
        using var author = await RegisterAsync("N1 Author");
        using var actor = await RegisterAsync("N1 Liker");
        var reviewId = await SeedReviewAsync(author.UserId, tmdbId);

        using var like = await PostAsync(actor.Client, $"/reviews/{reviewId}/like");
        Assert.Equal(HttpStatusCode.OK, like.StatusCode);

        var notification = await SingleNotificationForAsync(author.UserId);
        Assert.Equal(NotificationType.ReviewLiked, notification.Type);
        Assert.Equal(author.UserId, notification.RecipientUserId);
        Assert.Equal(actor.UserId, notification.ActorUserId);
        Assert.Equal(reviewId, notification.TargetId);
        Assert.False(notification.IsRead);
    }

    // N2 — a throwing IRealtimeNotifier must NOT fail the write: the like + notification still commit, and a
    // Warning is logged (best-effort push, §9.2). The throwing fake is injected via ConfigureTestServices.
    [Fact]
    public async Task N2_Push_failure_does_not_fail_the_write_and_logs_a_warning()
    {
        const int tmdbId = 950_002;

        using var throwingFactory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddScoped<IRealtimeNotifier, ThrowingRealtimeNotifier>()));

        using var author = await TestAuthentication.RegisterAndSignInAsync(throwingFactory, "N2 Author");
        using var actor = await TestAuthentication.RegisterAndSignInAsync(throwingFactory, "N2 Liker");
        var reviewId = await SeedReviewAsync(author.UserId, tmdbId);

        _factory.LogSink.Clear();

        using var like = await PostAsync(actor.Client, $"/reviews/{reviewId}/like");
        Assert.Equal(HttpStatusCode.OK, like.StatusCode);            // the push threw, but the like still succeeded

        // The write committed despite the realtime failure: the like row AND its notification are persisted.
        Assert.Equal(1, await LikeCountAsync(reviewId));
        Assert.True(await NotificationExistsAsync(author.UserId, reviewId, NotificationType.ReviewLiked));

        // A Warning was logged for the failed best-effort push (persistence is authoritative; the inbox is correct).
        var warned = _factory.LogSink.Snapshot().Any(logEvent =>
            logEvent.Level == LogEventLevel.Warning
            && logEvent.MessageTemplate.Text.Contains(
                "Best-effort realtime notification push", StringComparison.Ordinal));
        Assert.True(warned, "Expected a Warning for the failed best-effort realtime push.");
    }

    // N3 — an anonymous SignalR negotiate to /hubs/notifications is rejected (fail-closed [Authorize]): the
    // cookie handler challenges, so an unauthenticated caller gets a 401 or a 302 to login — never a 200. An
    // AUTHENTICATED negotiate succeeds (200), proving the hub is mapped and correctly auth-gated.
    [Fact]
    public async Task N3_Anonymous_hub_negotiate_is_rejected_and_authenticated_negotiate_succeeds()
    {
        using var anonymous = TestAuthentication.CreateClient(_factory);
        using var anonNegotiate = await anonymous.PostAsync("/hubs/notifications/negotiate?negotiateVersion=1", null);

        Assert.NotEqual(HttpStatusCode.OK, anonNegotiate.StatusCode);
        var rejected = anonNegotiate.StatusCode == HttpStatusCode.Unauthorized
            || (anonNegotiate.StatusCode == HttpStatusCode.Redirect
                && anonNegotiate.Headers.Location?.ToString().Contains("/account/login", StringComparison.OrdinalIgnoreCase) == true);
        Assert.True(rejected, $"Expected the anonymous negotiate to be rejected (401/redirect), got {(int)anonNegotiate.StatusCode}.");

        using var authed = await RegisterAsync("N3 Connector");
        using var authedNegotiate = await authed.Client.PostAsync("/hubs/notifications/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.OK, authedNegotiate.StatusCode);
    }

    // N4 — the inbox returns ONLY my notifications, newest-first, keyset-paginated with no overlap.
    [Fact]
    public async Task N4_Inbox_returns_only_my_notifications_newest_first_without_overlap()
    {
        using var me = await RegisterAsync("N4 Me");
        using var other = await RegisterAsync("N4 Other");

        var baseTime = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var mine0 = await SeedNotificationAsync(me.UserId, NotificationType.FriendRequest, "n4-mine-oldest", baseTime);
        var mine1 = await SeedNotificationAsync(me.UserId, NotificationType.FriendAccepted, "n4-mine-middle", baseTime.AddMinutes(1));
        var mine2 = await SeedNotificationAsync(me.UserId, NotificationType.CommentAdded, "n4-mine-newest", baseTime.AddMinutes(2));
        await SeedNotificationAsync(other.UserId, NotificationType.FriendRequest, "n4-other-a", baseTime.AddMinutes(3));
        await SeedNotificationAsync(other.UserId, NotificationType.ReviewLiked, "n4-other-b", baseTime.AddMinutes(4));

        // Page 1 (newest two of mine): the inbox shell.
        using var page1 = await me.Client.GetAsync("/notifications?take=2");
        Assert.Equal(HttpStatusCode.OK, page1.StatusCode);
        var p1 = await page1.Content.ReadAsStringAsync();

        Assert.Contains(mine2.Message, p1, StringComparison.Ordinal);        // newest present
        Assert.Contains(mine1.Message, p1, StringComparison.Ordinal);        // second-newest present
        Assert.DoesNotContain(mine0.Message, p1, StringComparison.Ordinal);  // oldest is on page 2
        Assert.DoesNotContain("n4-other-a", p1, StringComparison.Ordinal);   // never another user's
        Assert.DoesNotContain("n4-other-b", p1, StringComparison.Ordinal);

        // Page 2 via the cursor of page 1's last (oldest-shown) item.
        var url = $"/notifications/feed?take=2" +
                  $"&cursorCreatedAt={Uri.EscapeDataString(mine1.CreatedAtUtc.ToString("o"))}" +
                  $"&cursorId={mine1.Id}";
        using var page2 = await me.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, page2.StatusCode);
        var p2 = await page2.Content.ReadAsStringAsync();

        Assert.DoesNotContain("<html", p2, StringComparison.OrdinalIgnoreCase);  // partial only
        Assert.Contains(mine0.Message, p2, StringComparison.Ordinal);            // the remaining item
        Assert.DoesNotContain(mine1.Message, p2, StringComparison.Ordinal);      // no overlap
        Assert.DoesNotContain(mine2.Message, p2, StringComparison.Ordinal);
        Assert.DoesNotContain("n4-other-a", p2, StringComparison.Ordinal);
    }

    // N5 — marking a notification you don't own is NotFound (no cross-user mutation): the row stays unread.
    [Fact]
    public async Task N5_Marking_another_users_notification_read_is_not_found()
    {
        using var me = await RegisterAsync("N5 Me");
        using var owner = await RegisterAsync("N5 Owner");

        var seeded = await SeedNotificationAsync(
            owner.UserId, NotificationType.FriendRequest, "n5-owner-note",
            new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Unspecified));

        using var response = await PostAsync(me.Client, $"/notifications/{seeded.Id}/read");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        Assert.False(await NotificationIsReadAsync(seeded.Id));   // row unchanged (still unread)
    }

    // N6 — GET /notifications keeps the strict CSP unchanged: script-src 'self', connect-src 'self' (which admits
    // the same-origin hub WebSocket), and NO unsafe-eval.
    [Fact]
    public async Task N6_Notifications_page_keeps_the_strict_csp()
    {
        using var me = await RegisterAsync("N6 Me");

        using var response = await me.Client.GetAsync("/notifications");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var values));
        var csp = Assert.Single(values!);
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self'", csp, StringComparison.Ordinal);   // covers the same-origin hub WS
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
    }

    // Extra — the recipient CAN mark their own notification read (redirect + the row becomes read).
    [Fact]
    public async Task Owner_can_mark_their_own_notification_read()
    {
        using var me = await RegisterAsync("Mark Owner");
        var seeded = await SeedNotificationAsync(
            me.UserId, NotificationType.FriendAccepted, "own-note",
            new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Unspecified));

        using var response = await PostAsync(me.Client, $"/notifications/{seeded.Id}/read");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        Assert.True(await NotificationIsReadAsync(seeded.Id));
    }

    // Extra — mark-all marks every unread notification of the current user (and only theirs).
    [Fact]
    public async Task Mark_all_marks_every_unread_notification_of_the_current_user()
    {
        using var me = await RegisterAsync("MarkAll Me");
        using var other = await RegisterAsync("MarkAll Other");

        var baseTime = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Unspecified);
        var a = await SeedNotificationAsync(me.UserId, NotificationType.FriendRequest, "ma-a", baseTime);
        var b = await SeedNotificationAsync(me.UserId, NotificationType.CommentAdded, "ma-b", baseTime.AddMinutes(1));
        var theirs = await SeedNotificationAsync(other.UserId, NotificationType.FriendRequest, "ma-other", baseTime.AddMinutes(2));

        using var response = await PostAsync(me.Client, "/notifications/read-all");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        Assert.True(await NotificationIsReadAsync(a.Id));
        Assert.True(await NotificationIsReadAsync(b.Id));
        Assert.False(await NotificationIsReadAsync(theirs.Id));       // untouched — recipient-scoped
        Assert.Equal(0, await UnreadCountAsync(me.UserId));
    }

    // ---- HTTP helpers -------------------------------------------------------------------------------------

    private Task<AuthenticatedTestUser> RegisterAsync(string displayName) =>
        TestAuthentication.RegisterAndSignInAsync(_factory, displayName);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url)
    {
        var token = await TestAuthentication.AntiforgeryTokenAsync(client, "/discover");
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("HX-Request", "true");
        return await client.SendAsync(request);
    }

    // ---- persistence helpers ------------------------------------------------------------------------------

    private async Task<Guid> SeedReviewAsync(Guid authorId, int tmdbId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

        var movie = Movie.FromTmdb(tmdbId, MediaType.Movie, $"Seed Title {tmdbId}", null, null, null, null);
        db.Movies.Add(movie);
        var review = Review.Create(authorId, movie.Id, Rating.From(8), "Seed review body.");
        db.Reviews.Add(review);
        await db.SaveChangesAsync();
        return review.Id;
    }

    // Seeds one notification with an explicit CreatedAtUtc so the newest-first keyset order is deterministic.
    private async Task<SeededNotification> SeedNotificationAsync(
        Guid recipientUserId, NotificationType type, string message, DateTime createdAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

        var notification = Notification.Create(recipientUserId, type, message, actorUserId: null, targetId: null);
        db.Notifications.Add(notification);
        db.Entry(notification).Property(nameof(Notification.CreatedAtUtc)).CurrentValue = createdAt;
        await db.SaveChangesAsync();
        return new SeededNotification(notification.Id, createdAt, message);
    }

    private async Task<Notification> SingleNotificationForAsync(Guid recipientUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Notifications.AsNoTracking()
            .SingleAsync(notification => notification.RecipientUserId == recipientUserId);
    }

    private async Task<bool> NotificationExistsAsync(Guid recipientUserId, Guid targetId, NotificationType type)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Notifications.AsNoTracking().AnyAsync(notification =>
            notification.RecipientUserId == recipientUserId
            && notification.TargetId == targetId
            && notification.Type == type);
    }

    private async Task<bool> NotificationIsReadAsync(Guid notificationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Notifications.AsNoTracking()
            .Where(notification => notification.Id == notificationId)
            .Select(notification => notification.IsRead)
            .SingleAsync();
    }

    private async Task<int> UnreadCountAsync(Guid recipientUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Notifications.AsNoTracking()
            .CountAsync(notification => notification.RecipientUserId == recipientUserId && !notification.IsRead);
    }

    private async Task<int> LikeCountAsync(Guid reviewId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.ReviewLikes.AsNoTracking().CountAsync(like => like.ReviewId == reviewId);
    }

    private sealed record SeededNotification(Guid Id, DateTime CreatedAtUtc, string Message);

    // A deliberately failing realtime adapter — proves a push failure never rolls back or fails the write (N2).
    private sealed class ThrowingRealtimeNotifier : IRealtimeNotifier
    {
        public Task NotifyAsync(Guid recipientUserId, NotificationDto notification, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated realtime push failure (test).");

        public Task UnreadCountChangedAsync(Guid recipientUserId, int unreadCount, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated realtime push failure (test).");
    }
}
