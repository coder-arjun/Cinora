using System.Net;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Domain.ValueObjects;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Web.IntegrationTests.Reviews;

/// <summary>
/// Milestone 3.2 mock-first tests for the Likes + Comments slice, driven end-to-end through the real MVC +
/// hand-rolled <c>ISender</c> + Identity pipeline against the migrated <c>CinoraTest</c> LocalDB. They prove
/// the §6.3 plan L1–L6: idempotent like (L1), idempotent unlike (L2), self-like creates no notification (L3),
/// comment persists + <c>&lt;script&gt;</c> encoded (L4), cross-user comment delete → 403 (L5), and the
/// oldest-first keyset comment list (L6). Likes/comments touch no TMDB, so the shared factory is used directly;
/// the target review is seeded through the DbContext (author via <see cref="TestAuthentication"/> so the
/// identity + domain rows exist). Cinora is login-only (ADR 0023), so even the list-read test (L6) authenticates
/// a throwaway viewer. TMDB ids sit outside the seeded set to keep the shared database isolated.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class LikeCommentTests : IAsyncLifetime
{
    private const string ScriptPayload = "<script>alert('xss')</script>";

    private readonly CinoraWebApplicationFactory _factory;

    public LikeCommentTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // L1 — two likes for the same (user, review) yield ONE row (count 1); the author is notified exactly once.
    [Fact]
    public async Task L1_Like_is_idempotent_and_notifies_the_author_once()
    {
        const int tmdbId = 920_001;
        using var author = await RegisterAsync("Ada Author");
        using var actor = await RegisterAsync("Bob Liker");
        var reviewId = await SeedReviewAsync(author.UserId, tmdbId);

        using var like1 = await PostAsync(actor.Client, $"/reviews/{reviewId}/like");
        Assert.Equal(HttpStatusCode.OK, like1.StatusCode);
        using var like2 = await PostAsync(actor.Client, $"/reviews/{reviewId}/like");
        Assert.Equal(HttpStatusCode.OK, like2.StatusCode);

        var button = await like2.Content.ReadAsStringAsync();
        Assert.Contains("aria-pressed=\"true\"", button, StringComparison.Ordinal);

        Assert.Equal(1, await LikeCountAsync(reviewId));                    // one row, not two
        Assert.Equal(1, await NotificationCountAsync(author.UserId));       // notified once, not per-like
        Assert.True(await NotificationExistsAsync(author.UserId, reviewId, NotificationType.ReviewLiked));
    }

    // L2 — unlike removes the row; a second unlike is an idempotent 200 no-op.
    [Fact]
    public async Task L2_Unlike_removes_the_row_and_second_unlike_is_a_noop()
    {
        const int tmdbId = 920_002;
        using var author = await RegisterAsync("Carol Author");
        using var actor = await RegisterAsync("Dave Liker");
        var reviewId = await SeedReviewAsync(author.UserId, tmdbId);

        using var like = await PostAsync(actor.Client, $"/reviews/{reviewId}/like");
        Assert.Equal(HttpStatusCode.OK, like.StatusCode);
        Assert.Equal(1, await LikeCountAsync(reviewId));

        using var unlike1 = await DeleteAsync(actor.Client, $"/reviews/{reviewId}/like");
        Assert.Equal(HttpStatusCode.OK, unlike1.StatusCode);
        Assert.Equal(0, await LikeCountAsync(reviewId));

        using var unlike2 = await DeleteAsync(actor.Client, $"/reviews/{reviewId}/like");
        Assert.Equal(HttpStatusCode.OK, unlike2.StatusCode);               // idempotent no-op
        Assert.Equal(0, await LikeCountAsync(reviewId));
        var button = await unlike2.Content.ReadAsStringAsync();
        Assert.Contains("aria-pressed=\"false\"", button, StringComparison.Ordinal);
    }

    // L3 — liking your OWN review inserts the like but creates NO notification.
    [Fact]
    public async Task L3_Self_like_creates_no_notification()
    {
        const int tmdbId = 920_003;
        using var author = await RegisterAsync("Eve Selfliker");
        var reviewId = await SeedReviewAsync(author.UserId, tmdbId);

        using var like = await PostAsync(author.Client, $"/reviews/{reviewId}/like"); // likes own review
        Assert.Equal(HttpStatusCode.OK, like.StatusCode);

        Assert.Equal(1, await LikeCountAsync(reviewId));                              // the self-like inserted
        Assert.Equal(0, await NotificationCountAsync(author.UserId));                 // but NO notification
        Assert.False(await NotificationForTargetExistsAsync(reviewId, NotificationType.ReviewLiked));
    }

    // L4 — a comment persists with the server-resolved author; a <script> body renders HTML-encoded.
    [Fact]
    public async Task L4_Add_comment_persists_with_current_user_and_encodes_script()
    {
        const int tmdbId = 920_004;
        using var author = await RegisterAsync("Frank Author");
        using var actor = await RegisterAsync("Grace Commenter");
        var reviewId = await SeedReviewAsync(author.UserId, tmdbId);

        using var response = await AddCommentAsync(actor.Client, reviewId, ScriptPayload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var card = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script", card, StringComparison.OrdinalIgnoreCase); // encoded, not executable
        Assert.Contains("&lt;script&gt;", card, StringComparison.Ordinal);

        var comment = await SingleCommentAsync(reviewId);
        Assert.Equal(actor.UserId, comment.UserId);   // server-resolved author, not a bound field
        Assert.Equal(ScriptPayload, comment.Body);     // stored raw; encoded only at render time
        Assert.True(await NotificationExistsAsync(author.UserId, reviewId, NotificationType.CommentAdded));
    }

    // L5 — deleting another user's comment is 403 and leaves the row unchanged.
    [Fact]
    public async Task L5_Deleting_another_users_comment_is_forbidden()
    {
        const int tmdbId = 920_005;
        using var author = await RegisterAsync("Heidi Author");
        using var commenter = await RegisterAsync("Ivan Commenter");
        using var attacker = await RegisterAsync("Mallory");
        var reviewId = await SeedReviewAsync(author.UserId, tmdbId);

        using var add = await AddCommentAsync(commenter.Client, reviewId, "Ivan's comment.");
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var comment = await SingleCommentAsync(reviewId);

        using var delete = await DeleteAsync(attacker.Client, $"/comments/{comment.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);

        Assert.True(await CommentExistsAsync(comment.Id));         // row unchanged
        var after = await LoadCommentAsync(comment.Id);
        Assert.Equal("Ivan's comment.", after.Body);
        Assert.Equal(commenter.UserId, after.UserId);
    }

    // L6 — the comment thread is oldest-first and keyset-paginated with no overlap.
    [Fact]
    public async Task L6_Comments_list_is_keyset_paginated_without_overlap()
    {
        const int tmdbId = 920_006;
        using var author = await RegisterAsync("Judy Author");
        var reviewId = await SeedReviewAsync(author.UserId, tmdbId);
        string[] bodies = ["c-alpha", "c-bravo", "c-charlie", "c-delta", "c-echo"];
        var seeded = await SeedCommentsAsync(reviewId, author.UserId, bodies); // ascending time (0 oldest)

        using var viewer = await RegisterAsync("Comments Viewer");
        var client = viewer.Client;

        using var page1 = await client.GetAsync($"/reviews/{reviewId}/comments?take=2");
        Assert.Equal(HttpStatusCode.OK, page1.StatusCode);
        var p1 = await page1.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", p1, StringComparison.OrdinalIgnoreCase); // partial only

        // Oldest-first: the two earliest comments are on page 1; the third is not.
        Assert.Contains(bodies[0], p1, StringComparison.Ordinal);
        Assert.Contains(bodies[1], p1, StringComparison.Ordinal);
        Assert.DoesNotContain(bodies[2], p1, StringComparison.Ordinal);

        // Page 2 via the cursor of page 1's last item.
        var cursor = seeded[1];
        var url = $"/reviews/{reviewId}/comments?take=2" +
                  $"&cursorCreatedAt={Uri.EscapeDataString(cursor.CreatedAtUtc.ToString("o"))}" +
                  $"&cursorId={cursor.Id}";
        using var page2 = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, page2.StatusCode);
        var p2 = await page2.Content.ReadAsStringAsync();

        Assert.DoesNotContain(bodies[0], p2, StringComparison.Ordinal); // no overlap
        Assert.DoesNotContain(bodies[1], p2, StringComparison.Ordinal);
        Assert.Contains(bodies[2], p2, StringComparison.Ordinal);
        Assert.Contains(bodies[3], p2, StringComparison.Ordinal);
    }

    // L7 (Milestone 3.5 dedup) — like → unlike → like re-inserts the like row but does NOT create a SECOND
    // notification. The unlike removes only the ReviewLike row; the ReviewLiked notification persists, so the
    // re-like (a genuinely-new like, since the row was gone) hits LikeReviewCommand's dedup guard, finds the
    // existing notification for (recipient, actor, review), and skips re-notifying — exactly one, not two.
    // Guards the headline 3.5 obligation (the direct-double-like L1 short-circuits before the dedup branch).
    [Fact]
    public async Task L7_Relike_after_unlike_does_not_create_a_second_notification()
    {
        const int tmdbId = 920_007;
        using var author = await RegisterAsync("Nadia Author");
        using var actor = await RegisterAsync("Oscar Reliker");
        var reviewId = await SeedReviewAsync(author.UserId, tmdbId);

        using var like1 = await PostAsync(actor.Client, $"/reviews/{reviewId}/like");
        Assert.Equal(HttpStatusCode.OK, like1.StatusCode);
        using var unlike = await DeleteAsync(actor.Client, $"/reviews/{reviewId}/like");
        Assert.Equal(HttpStatusCode.OK, unlike.StatusCode);
        Assert.Equal(0, await LikeCountAsync(reviewId));                     // like row gone
        using var like2 = await PostAsync(actor.Client, $"/reviews/{reviewId}/like"); // genuinely-new re-like
        Assert.Equal(HttpStatusCode.OK, like2.StatusCode);

        Assert.Equal(1, await LikeCountAsync(reviewId));                     // the re-like inserted
        Assert.Equal(1, await NotificationCountAsync(author.UserId));        // still ONE notification, not two
        Assert.True(await NotificationExistsAsync(author.UserId, reviewId, NotificationType.ReviewLiked));
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

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, string url)
    {
        var token = await TestAuthentication.AntiforgeryTokenAsync(client, "/discover");
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("HX-Request", "true");
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> AddCommentAsync(HttpClient client, Guid reviewId, string body)
    {
        var token = await TestAuthentication.AntiforgeryTokenAsync(client, "/discover");
        var fields = new Dictionary<string, string>
        {
            ["Body"] = body,
            ["__RequestVerificationToken"] = token,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/reviews/{reviewId}/comments")
        {
            Content = new FormUrlEncodedContent(fields),
        };
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

    // Seeds comments with explicit ascending CreatedAtUtc so the oldest-first keyset order is deterministic.
    // Comments have no uniqueness constraint, so one author may post them all. Returns ascending-time order.
    private async Task<IReadOnlyList<SeededComment>> SeedCommentsAsync(Guid reviewId, Guid authorId, string[] bodies)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

        var baseTime = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var seeded = new List<SeededComment>(bodies.Length);

        for (var i = 0; i < bodies.Length; i++)
        {
            var comment = Comment.Create(reviewId, authorId, bodies[i]);
            db.Comments.Add(comment);
            var createdAt = baseTime.AddMinutes(i);
            db.Entry(comment).Property(nameof(Comment.CreatedAtUtc)).CurrentValue = createdAt;
            seeded.Add(new SeededComment(comment.Id, createdAt, bodies[i]));
        }

        await db.SaveChangesAsync();
        return seeded;
    }

    private async Task<int> LikeCountAsync(Guid reviewId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.ReviewLikes.AsNoTracking().CountAsync(like => like.ReviewId == reviewId);
    }

    private async Task<int> NotificationCountAsync(Guid recipientUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Notifications.AsNoTracking()
            .CountAsync(notification => notification.RecipientUserId == recipientUserId);
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

    private async Task<bool> NotificationForTargetExistsAsync(Guid targetId, NotificationType type)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Notifications.AsNoTracking()
            .AnyAsync(notification => notification.TargetId == targetId && notification.Type == type);
    }

    private async Task<Comment> SingleCommentAsync(Guid reviewId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Comments.AsNoTracking().SingleAsync(comment => comment.ReviewId == reviewId);
    }

    private async Task<Comment> LoadCommentAsync(Guid commentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Comments.AsNoTracking().SingleAsync(comment => comment.Id == commentId);
    }

    private async Task<bool> CommentExistsAsync(Guid commentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Comments.AsNoTracking().AnyAsync(comment => comment.Id == commentId);
    }

    private sealed record SeededComment(Guid Id, DateTime CreatedAtUtc, string Body);
}
