using System.Net;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Tmdb;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Domain.ValueObjects;
using Cinora.Infrastructure.Identity;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

namespace Cinora.Web.IntegrationTests.Reviews;

/// <summary>
/// Milestone 3.1 mock-first tests for the Reviews CRUD slice, driven end-to-end through the real MVC +
/// hand-rolled <c>ISender</c> + Identity pipeline against the migrated <c>CinoraTest</c> LocalDB with a
/// <see cref="FakeTmdbClient"/> replacing the network. They prove the §5.4 plan R1–R7: the server-resolved
/// author (R1), one review per title (R2), own-only edit → 403 (R3), delete cascade (R4), the keyset
/// list (R5), HTML-encoded bodies (R6), and rating bounds → 400 (R7).
/// </summary>
/// <remarks>
/// Every test establishes real Identity cookies via <see cref="TestAuthentication"/> (registration
/// through the actual HTTP flow — the cookie is what <c>ICurrentUser</c> resolves the acting user from; there
/// is no fake sign-in shortcut) — Cinora is login-only (ADR 0023), so even the list-read tests authenticate a
/// throwaway viewer. Tests that need a persisted title use a <c>WithWebHostBuilder</c>-derived host
/// (fake TMDB), saving/restoring the process-global Serilog logger the sibling suites rely on; the list-read
/// tests seed a title + reviews directly through the DbContext (one review per user per title) and use the
/// shared factory. TMDB ids sit outside the seeded set so the shared database stays isolated.
/// </remarks>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class ReviewTests : IAsyncLifetime
{
    private const string ScriptPayload = "<script>alert('xss')</script>";

    private readonly CinoraWebApplicationFactory _factory;

    public ReviewTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // R1 — the created review is stamped with the SERVER-resolved user; a hidden UserId=other is ignored.
    [Fact]
    public async Task R1_Create_review_stamps_the_current_user_and_ignores_a_spoofed_user_id()
    {
        const int tmdbId = 910_001;
        using var factory = CreateFactoryWith(FakeWithMovie(tmdbId));
        using var author = await TestAuthentication.RegisterAndSignInAsync(factory, "Ada Author");
        var spoofedUserId = Guid.NewGuid();

        var token = await TestAuthentication.AntiforgeryTokenAsync(author.Client, "/discover");
        using var response = await PostCreateAsync(
            author.Client, token, tmdbId, rating: 8, body: "A stellar film.",
            extraFields: new Dictionary<string, string> { ["UserId"] = spoofedUserId.ToString() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", html, StringComparison.OrdinalIgnoreCase); // the _ReviewCard partial
        Assert.Contains("A stellar film.", html, StringComparison.Ordinal);

        var review = await SingleReviewForTmdbAsync(factory, tmdbId);
        Assert.Equal(author.UserId, review.UserId);      // the actor came from the auth cookie, server-side
        Assert.NotEqual(spoofedUserId, review.UserId);   // the bound-in UserId field was never honoured
        Assert.Equal(8, review.Rating.Value);
    }

    // R2 — a second review for the same (user, title) does not duplicate; the friendly result returns 200.
    [Fact]
    public async Task R2_Second_review_for_the_same_title_does_not_duplicate()
    {
        const int tmdbId = 910_002;
        using var factory = CreateFactoryWith(FakeWithMovie(tmdbId));
        using var author = await TestAuthentication.RegisterAndSignInAsync(factory, "Bob Author");

        var token1 = await TestAuthentication.AntiforgeryTokenAsync(author.Client, "/discover");
        using var first = await PostCreateAsync(author.Client, token1, tmdbId, 7, "First take.");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var token2 = await TestAuthentication.AntiforgeryTokenAsync(author.Client, "/discover");
        using var second = await PostCreateAsync(author.Client, token2, tmdbId, 3, "Second take.");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode); // friendly already-reviewed, not an error

        Assert.Equal(1, await ReviewCountForTmdbAsync(factory, tmdbId, author.UserId)); // no duplicate row

        // The surviving review is the FIRST one — the second create was a no-op, never an overwrite.
        var review = await SingleReviewForTmdbAsync(factory, tmdbId);
        Assert.Equal(7, review.Rating.Value);
        Assert.Equal("First take.", review.Body);
    }

    // R3 — user B editing user A's review is 403, and A's row is left completely unchanged.
    [Fact]
    public async Task R3_Editing_another_users_review_is_forbidden_and_leaves_it_unchanged()
    {
        const int tmdbId = 910_003;
        using var factory = CreateFactoryWith(FakeWithMovie(tmdbId));

        using var author = await TestAuthentication.RegisterAndSignInAsync(factory, "Carol Author");
        var tokenA = await TestAuthentication.AntiforgeryTokenAsync(author.Client, "/discover");
        using var create = await PostCreateAsync(author.Client, tokenA, tmdbId, 9, "Author's own words.");
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var review = await SingleReviewForTmdbAsync(factory, tmdbId);

        using var attacker = await TestAuthentication.RegisterAndSignInAsync(factory, "Mallory");
        var tokenB = await TestAuthentication.AntiforgeryTokenAsync(attacker.Client, "/discover");
        using var edit = await PostEditAsync(attacker.Client, tokenB, review.Id, rating: 1, body: "Defaced.");

        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);

        var after = await LoadReviewAsync(factory, review.Id);
        Assert.Equal(9, after.Rating.Value);              // rating unchanged
        Assert.Equal("Author's own words.", after.Body);  // body unchanged
        Assert.Null(after.UpdatedAtUtc);                  // never mutated
    }

    // R4 — deleting your own review removes it and, via the FK cascade, its likes and comments.
    [Fact]
    public async Task R4_Deleting_own_review_cascades_its_likes_and_comments()
    {
        const int tmdbId = 910_004;
        using var factory = CreateFactoryWith(FakeWithMovie(tmdbId));

        using var author = await TestAuthentication.RegisterAndSignInAsync(factory, "Dana Author");
        var token = await TestAuthentication.AntiforgeryTokenAsync(author.Client, "/discover");
        using var create = await PostCreateAsync(author.Client, token, tmdbId, 6, "This will be deleted.");
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var review = await SingleReviewForTmdbAsync(factory, tmdbId);

        await SeedLikeAndCommentAsync(factory, review.Id, author.UserId);
        Assert.True(await ReviewLikeExistsAsync(factory, review.Id));
        Assert.True(await CommentExistsAsync(factory, review.Id));

        var deleteToken = await TestAuthentication.AntiforgeryTokenAsync(author.Client, "/discover");
        using var delete = await DeleteReviewAsync(author.Client, deleteToken, review.Id);
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        Assert.False(await ReviewExistsAsync(factory, review.Id));       // gone
        Assert.False(await ReviewLikeExistsAsync(factory, review.Id));   // cascade
        Assert.False(await CommentExistsAsync(factory, review.Id));      // cascade
    }

    // R5 — the reviews list is newest-first and keyset-paginated with no overlap across the boundary.
    [Fact]
    public async Task R5_Reviews_list_is_keyset_paginated_without_overlap()
    {
        const int tmdbId = 910_005;
        string[] bodies =
        [
            "Seeded-review-alpha", "Seeded-review-bravo", "Seeded-review-charlie",
            "Seeded-review-delta", "Seeded-review-echo",
        ];
        var seeded = await SeedTitleWithReviewsAsync(_factory, tmdbId, bodies); // ascending time (0 oldest)

        using var viewer = await TestAuthentication.RegisterAndSignInAsync(_factory, "Reviews Viewer");
        var client = viewer.Client;

        using var page1 = await client.GetAsync($"/discover/title/movie/{tmdbId}/reviews?take=2");
        Assert.Equal(HttpStatusCode.OK, page1.StatusCode);
        var page1Body = await page1.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", page1Body, StringComparison.OrdinalIgnoreCase); // partial only

        // Newest-first: the two most-recent bodies are on page 1; the third-newest is not.
        Assert.Contains(bodies[4], page1Body, StringComparison.Ordinal);
        Assert.Contains(bodies[3], page1Body, StringComparison.Ordinal);
        Assert.DoesNotContain(bodies[2], page1Body, StringComparison.Ordinal);

        // Page 2 via the cursor of page 1's last (second-newest) item.
        var cursor = seeded[3];
        var url = $"/discover/title/movie/{tmdbId}/reviews?take=2" +
                  $"&cursorCreatedAt={Uri.EscapeDataString(cursor.CreatedAtUtc.ToString("o"))}" +
                  $"&cursorId={cursor.Id}";
        using var page2 = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, page2.StatusCode);
        var page2Body = await page2.Content.ReadAsStringAsync();

        // No overlap with page 1, and the next two older items appear.
        Assert.DoesNotContain(bodies[4], page2Body, StringComparison.Ordinal);
        Assert.DoesNotContain(bodies[3], page2Body, StringComparison.Ordinal);
        Assert.Contains(bodies[2], page2Body, StringComparison.Ordinal);
        Assert.Contains(bodies[1], page2Body, StringComparison.Ordinal);
    }

    // R6 — a body containing <script> renders HTML-encoded (no executable markup; no Html.Raw path).
    [Fact]
    public async Task R6_Review_body_containing_script_is_html_encoded()
    {
        const int tmdbId = 910_006;
        await SeedTitleWithReviewsAsync(_factory, tmdbId, [ScriptPayload]);

        using var viewer = await TestAuthentication.RegisterAndSignInAsync(_factory, "Reviews Viewer");
        var client = viewer.Client;
        using var response = await client.GetAsync($"/discover/title/movie/{tmdbId}/reviews");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // The raw tag must NOT appear anywhere; its output-encoded form must.
        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", body, StringComparison.Ordinal);
    }

    // R7 — a rating outside 1–10 is rejected with 400 (the validator, backstopped by the domain guard).
    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task R7_Rating_out_of_range_is_rejected_with_400(int rating)
    {
        const int tmdbId = 910_007;
        using var factory = CreateFactoryWith(FakeWithMovie(tmdbId));
        using var author = await TestAuthentication.RegisterAndSignInAsync(factory, "Eve Author");
        var token = await TestAuthentication.AntiforgeryTokenAsync(author.Client, "/discover");

        using var response = await PostCreateAsync(author.Client, token, tmdbId, rating, "Out-of-range rating.");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- HTTP helpers -------------------------------------------------------------------------------------

    private static async Task<HttpResponseMessage> PostCreateAsync(
        HttpClient client,
        string token,
        int tmdbId,
        int rating,
        string body,
        IReadOnlyDictionary<string, string>? extraFields = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["TmdbId"] = tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Media"] = nameof(MediaType.Movie),
            ["Rating"] = rating.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Body"] = body,
            ["__RequestVerificationToken"] = token,
        };
        if (extraFields is not null)
        {
            foreach (var (key, value) in extraFields)
            {
                fields[key] = value;
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/reviews")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        request.Headers.Add("HX-Request", "true"); // ask for the _ReviewCard partial rather than a redirect
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostEditAsync(
        HttpClient client, string token, Guid reviewId, int rating, string body)
    {
        var fields = new Dictionary<string, string>
        {
            ["Rating"] = rating.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Body"] = body,
            ["__RequestVerificationToken"] = token,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/reviews/{reviewId}")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        request.Headers.Add("HX-Request", "true");
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> DeleteReviewAsync(HttpClient client, string token, Guid reviewId)
    {
        // hx-delete carries the anti-forgery token in the RequestVerificationToken header (no form body).
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/reviews/{reviewId}");
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("HX-Request", "true");
        return client.SendAsync(request);
    }

    // ---- persistence helpers ------------------------------------------------------------------------------

    private static FakeTmdbClient FakeWithMovie(int tmdbId)
    {
        var fake = new FakeTmdbClient();
        fake.Details[(MediaType.Movie, tmdbId)] = new TmdbTitleDetails
        {
            TmdbId = tmdbId,
            MediaType = MediaType.Movie,
            Title = $"Reviewable Title {tmdbId}",
            Overview = "A canned overview.",
            Genres = [],
        };
        return fake;
    }

    private static async Task<Review> SingleReviewForTmdbAsync(WebApplicationFactory<Program> factory, int tmdbId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var movieId = await MovieIdAsync(db, tmdbId);
        return await db.Reviews.AsNoTracking().SingleAsync(review => review.MovieId == movieId);
    }

    private static async Task<int> ReviewCountForTmdbAsync(
        WebApplicationFactory<Program> factory, int tmdbId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var movieId = await MovieIdAsync(db, tmdbId);
        return await db.Reviews.AsNoTracking()
            .CountAsync(review => review.MovieId == movieId && review.UserId == userId);
    }

    private static async Task<Review> LoadReviewAsync(WebApplicationFactory<Program> factory, Guid reviewId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Reviews.AsNoTracking().SingleAsync(review => review.Id == reviewId);
    }

    private static async Task<bool> ReviewExistsAsync(WebApplicationFactory<Program> factory, Guid reviewId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Reviews.AsNoTracking().AnyAsync(review => review.Id == reviewId);
    }

    private static async Task<bool> ReviewLikeExistsAsync(WebApplicationFactory<Program> factory, Guid reviewId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.ReviewLikes.AsNoTracking().AnyAsync(like => like.ReviewId == reviewId);
    }

    private static async Task<bool> CommentExistsAsync(WebApplicationFactory<Program> factory, Guid reviewId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Comments.AsNoTracking().AnyAsync(comment => comment.ReviewId == reviewId);
    }

    private static async Task SeedLikeAndCommentAsync(
        WebApplicationFactory<Program> factory, Guid reviewId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        db.ReviewLikes.Add(ReviewLike.Create(reviewId, userId));
        db.Comments.Add(Comment.Create(reviewId, userId, "A seeded comment."));
        await db.SaveChangesAsync();
    }

    // Seeds a title plus one review per body (distinct authors — the unique (UserId, MovieId) allows only one
    // review per user), stamping explicit ascending CreatedAtUtc so the keyset ordering is deterministic.
    // Each author is created through the atomic registration service so BOTH the identity (AspNetUsers) and
    // domain (Users) rows exist — a Review's UserId FK chains through Users → AspNetUsers. Returns the reviews
    // in ascending-time order (index 0 = oldest).
    private static async Task<IReadOnlyList<SeededReview>> SeedTitleWithReviewsAsync(
        WebApplicationFactory<Program> factory, int tmdbId, string[] bodies)
    {
        var authorIds = new List<Guid>(bodies.Length);
        for (var i = 0; i < bodies.Length; i++)
        {
            authorIds.Add(await RegisterUserAsync(factory, $"Seeded Author {i}"));
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

        var movie = Movie.FromTmdb(tmdbId, MediaType.Movie, $"Seeded Title {tmdbId}", null, null, null, null);
        db.Movies.Add(movie);

        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var seeded = new List<SeededReview>(bodies.Length);

        for (var i = 0; i < bodies.Length; i++)
        {
            var review = Review.Create(authorIds[i], movie.Id, Rating.From((i % 10) + 1), bodies[i]);
            db.Reviews.Add(review);

            var createdAt = baseTime.AddMinutes(i);
            db.Entry(review).Property(nameof(Review.CreatedAtUtc)).CurrentValue = createdAt;

            seeded.Add(new SeededReview(review.Id, createdAt, bodies[i]));
        }

        await db.SaveChangesAsync();
        return seeded;
    }

    // Creates a review author through the atomic registration seam (identity + domain rows share one PK).
    private static async Task<Guid> RegisterUserAsync(WebApplicationFactory<Program> factory, string displayName)
    {
        using var scope = factory.Services.CreateScope();
        var registration = scope.ServiceProvider.GetRequiredService<IUserRegistrationService>();
        var result = await registration.RegisterAsync(
            $"seed-{Guid.NewGuid():N}@cinora.test", "Test1234!", $"{displayName} {Guid.NewGuid():N}", CancellationToken.None);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Seed registration for '{displayName}' failed.");
        }

        return result.UserId;
    }

    private static Task<Guid> MovieIdAsync(CinoraDbContext db, int tmdbId) =>
        db.Movies.AsNoTracking()
            .Where(movie => movie.TmdbId == tmdbId && movie.MediaType == MediaType.Movie)
            .Select(movie => movie.Id)
            .SingleAsync();

    // Builds a fake-backed derived host (leaving the shared factory untouched) and restores the process-global
    // Serilog logger that building the host installs, matching the sibling suites in this non-parallel collection.
    private WebApplicationFactory<Program> CreateFactoryWith(ITmdbClient fake)
    {
        var originalLogger = Log.Logger;
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITmdbClient>();
                services.AddScoped(_ => fake);
            }));

        try
        {
            _ = factory.Services;
        }
        finally
        {
            Log.Logger = originalLogger;
        }

        return factory;
    }

    private sealed record SeededReview(Guid Id, DateTime CreatedAtUtc, string Body);
}
