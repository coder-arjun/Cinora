using System.Net;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Domain.ValueObjects;
using Cinora.Infrastructure.Identity;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Web.IntegrationTests.Feed;

/// <summary>
/// Milestone 3.4 mock-first tests for the activity feed slice, driven end-to-end through the real MVC +
/// hand-rolled <c>ISender</c> + Identity pipeline against the migrated <c>CinoraTest</c> LocalDB. They prove the
/// §8 plan A1–A4: the feed shows only accepted friends' reviews — excluding non-friends and the viewer's own
/// (A1); it is keyset-paginated with no overlap across the page boundary (A2); it requires authentication (A3);
/// and a user with no friends sees the find-friends empty state, not an error (A4). The feed touches no TMDB, so
/// the shared factory is used directly; users are created through <see cref="TestAuthentication"/> / the atomic
/// registration seam so the identity + domain rows exist and reviews' FKs resolve. TMDB ids sit outside other
/// suites' seeded sets to keep the shared database isolated.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class ActivityFeedTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public ActivityFeedTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // A1 — the feed shows a friend's review, but never a non-friend's, and never the viewer's own.
    [Fact]
    public async Task A1_Feed_shows_only_accepted_friends_reviews()
    {
        const int tmdbId = 940_001;
        const string friendBody = "Bob-friend-review-marker-A1";
        const string strangerBody = "Carol-stranger-review-marker-A1";
        const string ownBody = "Alice-own-review-marker-A1";

        using var alice = await RegisterAsync("Alice A1");                 // the viewer (authed client)
        var bob = await RegisterServerUserAsync("Bob Friend A1");          // an accepted friend
        var carol = await RegisterServerUserAsync("Carol Stranger A1");    // NOT a friend

        var movieId = await SeedMovieAsync(tmdbId);
        await SeedReviewAsync(bob, movieId, friendBody);
        await SeedReviewAsync(carol, movieId, strangerBody);
        await SeedReviewAsync(alice.UserId, movieId, ownBody);
        await MakeAcceptedFriendsAsync(alice.UserId, bob);

        using var response = await alice.Client.GetAsync("/home");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains(friendBody, html, StringComparison.Ordinal);          // friend's review is shown
        Assert.DoesNotContain(strangerBody, html, StringComparison.Ordinal);  // a non-friend's is not
        Assert.DoesNotContain(ownBody, html, StringComparison.Ordinal);       // the viewer's own is excluded
    }

    // A2 — page 2 (via the cursor of page 1's last item) returns strictly older items with no overlap and no gap.
    [Fact]
    public async Task A2_Feed_is_keyset_paginated_no_overlap()
    {
        // One friend reviews 21 distinct titles (unique (UserId, MovieId) allows one review per title) with
        // ascending CreatedAtUtc, so the keyset ordering is deterministic. Page size is 20, so page 1 holds the
        // 20 newest (indices 20..1) and page 2 — via the cursor of page 1's last item (index 1) — holds only the
        // oldest (index 0).
        const int total = 21;
        var baseTime = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Unspecified);

        using var alice = await RegisterAsync("Alice A2");
        var bob = await RegisterServerUserAsync("Bob Prolific A2");
        await MakeAcceptedFriendsAsync(alice.UserId, bob);
        var seeded = await SeedReviewsOnDistinctMoviesAsync(bob, total, baseTmdbId: 941_000, baseTime);

        // Page 1 via /home: the 20 newest; the oldest (index 0) is NOT present.
        using var page1 = await alice.Client.GetAsync("/home");
        Assert.Equal(HttpStatusCode.OK, page1.StatusCode);
        var page1Html = await page1.Content.ReadAsStringAsync();
        Assert.Contains(seeded[20].Body, page1Html, StringComparison.Ordinal); // newest, on page 1
        Assert.Contains(seeded[1].Body, page1Html, StringComparison.Ordinal);  // 20th-newest = page 1's last
        Assert.DoesNotContain(seeded[0].Body, page1Html, StringComparison.Ordinal); // oldest, not yet

        // Page 2 via the cursor of page 1's last item (index 1): strictly older → only index 0, no overlap.
        var cursor = seeded[1];
        var url = $"/home/feed?cursorCreatedAt={Uri.EscapeDataString(cursor.CreatedAt.ToString("o"))}" +
                  $"&cursorId={cursor.Id}";
        using var page2 = await alice.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, page2.StatusCode);
        var page2Html = await page2.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", page2Html, StringComparison.OrdinalIgnoreCase); // partial only, no layout

        Assert.Contains(seeded[0].Body, page2Html, StringComparison.Ordinal);          // the next older item appears
        Assert.DoesNotContain(seeded[1].Body, page2Html, StringComparison.Ordinal);    // no overlap with page 1
        Assert.DoesNotContain(seeded[20].Body, page2Html, StringComparison.Ordinal);   // no overlap with page 1
    }

    // A3 — an anonymous GET of /home is redirected to login (fail-closed authorization).
    [Fact]
    public async Task A3_Feed_requires_auth()
    {
        using var client = TestAuthentication.CreateClient(_factory); // anonymous — no sign-in

        using var response = await client.GetAsync("/home");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/account/login",
            response.Headers.Location?.OriginalString ?? string.Empty,
            StringComparison.Ordinal);
    }

    // A4 — a user with no friends sees the find-friends empty state with HTTP 200, not an error.
    [Fact]
    public async Task A4_No_friends_shows_empty_state()
    {
        using var loner = await RegisterAsync("Loner A4"); // freshly registered, zero friends

        using var response = await loner.Client.GetAsync("/home");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // The find-friends empty state carries a stable marker (decoupled from the copy); it is not an error page.
        Assert.Contains("data-feed-empty=\"no-friends\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-feed-empty=\"no-activity\"", html, StringComparison.Ordinal);
    }

    // ---- seed helpers -------------------------------------------------------------------------------------

    private Task<AuthenticatedTestUser> RegisterAsync(string displayName) =>
        TestAuthentication.RegisterAndSignInAsync(_factory, displayName);

    // Creates a user through the atomic registration seam (identity + domain rows share one PK) without needing
    // an authenticated client — used for the feed's friend/author users the test never posts as.
    private async Task<Guid> RegisterServerUserAsync(string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var registration = scope.ServiceProvider.GetRequiredService<IUserRegistrationService>();
        var result = await registration.RegisterAsync(
            $"feed-{Guid.NewGuid():N}@cinora.test", "Test1234!", $"{displayName} {Guid.NewGuid():N}", CancellationToken.None);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Seed registration for '{displayName}' failed.");
        }

        return result.UserId;
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

    private async Task<Guid> SeedMovieAsync(int tmdbId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var movie = Movie.FromTmdb(tmdbId, MediaType.Movie, $"Feed Title {tmdbId}", null, null, null, null);
        db.Movies.Add(movie);
        await db.SaveChangesAsync();
        return movie.Id;
    }

    private async Task SeedReviewAsync(Guid authorId, Guid movieId, string body)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        db.Reviews.Add(Review.Create(authorId, movieId, Rating.From(8), body));
        await db.SaveChangesAsync();
    }

    // Seeds one author's reviews across `count` distinct titles with explicit ascending CreatedAtUtc (index 0 =
    // oldest), in a single unit of work. Returns the reviews in ascending-time order for keyset assertions.
    private async Task<IReadOnlyList<SeededFeedReview>> SeedReviewsOnDistinctMoviesAsync(
        Guid authorId, int count, int baseTmdbId, DateTime baseTime)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

        var seeded = new List<SeededFeedReview>(count);
        for (var i = 0; i < count; i++)
        {
            var movie = Movie.FromTmdb(
                baseTmdbId + i, MediaType.Movie, $"Feed Title {baseTmdbId + i}", null, null, null, null);
            db.Movies.Add(movie);

            var body = $"feed-review-{i:D2}";
            var review = Review.Create(authorId, movie.Id, Rating.From((i % 10) + 1), body);
            db.Reviews.Add(review);

            var createdAt = baseTime.AddMinutes(i);
            db.Entry(review).Property(nameof(Review.CreatedAtUtc)).CurrentValue = createdAt;

            seeded.Add(new SeededFeedReview(review.Id, createdAt, body));
        }

        await db.SaveChangesAsync();
        return seeded;
    }

    private sealed record SeededFeedReview(Guid Id, DateTime CreatedAt, string Body);
}
