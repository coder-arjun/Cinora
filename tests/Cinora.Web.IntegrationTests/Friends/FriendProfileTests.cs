using System.Net;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Domain.ValueObjects;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Web.IntegrationTests.Friends;

/// <summary>
/// Milestone 3.3 mock-first tests for the Friends + profiles slice, driven end-to-end through the real MVC +
/// hand-rolled <c>ISender</c> + Identity pipeline against the migrated <c>CinoraTest</c> LocalDB. They prove the
/// §7.5 plan F1–F6: request creates a pending row + notifies the addressee (F1), no self-friend (F2), the
/// directed-pair guard creates no reciprocal second row (F3), addressee-only response with a 403 otherwise + an
/// accept notification to the requester (F4), private-profile review hiding for a non-friend (F5), and the
/// anonymous profile redirect to login (F6). Friends/profiles touch no TMDB, so the shared factory is used
/// directly; users are created through <see cref="TestAuthentication"/> so the identity + domain rows exist.
/// TMDB ids sit outside other suites' seeded sets to keep the shared database isolated.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class FriendProfileTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public FriendProfileTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // F1 — sending a request creates a pending Friend row and co-persists a FriendRequest notification to the addressee.
    [Fact]
    public async Task F1_Send_request_creates_pending_row_and_notifies_addressee()
    {
        using var requester = await RegisterAsync("Ada Requester");
        using var addressee = await RegisterAsync("Bob Addressee");

        using var response = await SendRequestAsync(requester.Client, addressee.UserId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var friend = await FriendRowAsync(requester.UserId, addressee.UserId);
        Assert.NotNull(friend);
        Assert.Equal(FriendStatus.Pending, friend!.Status);

        // The FriendRequest notification goes to the addressee, from the requester, deep-linking the request row.
        Assert.True(await NotificationExistsAsync(
            recipientUserId: addressee.UserId,
            actorUserId: requester.UserId,
            targetId: friend.Id,
            type: NotificationType.FriendRequest));
    }

    // F2 — sending a request to yourself is rejected with 400 (the domain guard) and creates no row.
    [Fact]
    public async Task F2_Cannot_friend_yourself()
    {
        using var user = await RegisterAsync("Solo User");

        using var response = await SendRequestAsync(user.Client, user.UserId);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(0, await FriendCountForUserAsync(user.UserId));
    }

    // F3 — if B→A is already pending, A's send returns ReciprocalPending and creates no second (A→B) row.
    [Fact]
    public async Task F3_Reciprocal_request_does_not_create_a_second_row()
    {
        using var userA = await RegisterAsync("Alice");
        using var userB = await RegisterAsync("Bruno");

        // B → A first.
        using var first = await SendRequestAsync(userB.Client, userA.UserId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // A → B now: the pair guard must NOT create a mirror row; it surfaces "respond instead".
        using var second = await SendRequestAsync(userA.Client, userB.UserId);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var message = await second.Content.ReadAsStringAsync();
        Assert.Contains("respond in your requests", message, StringComparison.Ordinal);

        // Exactly ONE Friend row for the unordered pair — still the original B → A request.
        Assert.Equal(1, await FriendCountForPairAsync(userA.UserId, userB.UserId));
        var only = await FriendRowAsync(userB.UserId, userA.UserId);
        Assert.NotNull(only);
        Assert.Equal(FriendStatus.Pending, only!.Status);
    }

    // F4 — only the addressee may respond (requester/third-party → 403); the addressee's accept flips the row to
    // Accepted and co-persists a FriendAccepted notification to the requester.
    [Fact]
    public async Task F4_Only_addressee_can_respond_and_accept_notifies_requester()
    {
        using var requester = await RegisterAsync("Carol Requester");
        using var addressee = await RegisterAsync("Dan Addressee");
        using var thirdParty = await RegisterAsync("Mallory");

        using var send = await SendRequestAsync(requester.Client, addressee.UserId);
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        var friend = await FriendRowAsync(requester.UserId, addressee.UserId);
        Assert.NotNull(friend);

        // The requester cannot accept their own outgoing request.
        using var byRequester = await PostWithTokenAsync(requester.Client, $"/friends/requests/{friend!.Id}/accept");
        Assert.Equal(HttpStatusCode.Forbidden, byRequester.StatusCode);

        // Neither can an unrelated third party.
        using var byThirdParty = await PostWithTokenAsync(thirdParty.Client, $"/friends/requests/{friend.Id}/accept");
        Assert.Equal(HttpStatusCode.Forbidden, byThirdParty.StatusCode);

        // Still pending after the forbidden attempts.
        Assert.Equal(FriendStatus.Pending, (await FriendRowAsync(requester.UserId, addressee.UserId))!.Status);

        // The addressee accepts.
        using var byAddressee = await PostWithTokenAsync(addressee.Client, $"/friends/requests/{friend.Id}/accept");
        Assert.Equal(HttpStatusCode.OK, byAddressee.StatusCode);

        Assert.Equal(FriendStatus.Accepted, (await FriendRowAsync(requester.UserId, addressee.UserId))!.Status);
        Assert.True(await NotificationExistsAsync(
            recipientUserId: requester.UserId,
            actorUserId: addressee.UserId,
            targetId: friend.Id,
            type: NotificationType.FriendAccepted));
    }

    // F5 — a signed-in non-friend sees the LIMITED profile (no reviews) when the owner is private, and the
    // reviews when the owner is public.
    [Fact]
    public async Task F5_Private_profile_hides_reviews_from_a_non_friend()
    {
        const int tmdbId = 930_005;
        const string reviewBody = "Owner-private-review-marker-F5";

        using var owner = await RegisterAsync("Olivia Owner");
        await SeedReviewAsync(owner.UserId, tmdbId, reviewBody);
        using var viewer = await RegisterAsync("Victor Viewer"); // a non-friend

        // Private: the review must NOT appear; the limited/private notice does.
        await SetProfileVisibilityAsync(owner.UserId, isPublic: false);
        using var privateView = await viewer.Client.GetAsync($"/users/{owner.UserId}");
        Assert.Equal(HttpStatusCode.OK, privateView.StatusCode);
        var privateHtml = await privateView.Content.ReadAsStringAsync();
        Assert.DoesNotContain(reviewBody, privateHtml, StringComparison.Ordinal);
        Assert.Contains("private", privateHtml, StringComparison.OrdinalIgnoreCase);

        // Public: the same non-friend now sees the review.
        await SetProfileVisibilityAsync(owner.UserId, isPublic: true);
        using var publicView = await viewer.Client.GetAsync($"/users/{owner.UserId}");
        Assert.Equal(HttpStatusCode.OK, publicView.StatusCode);
        var publicHtml = await publicView.Content.ReadAsStringAsync();
        Assert.Contains(reviewBody, publicHtml, StringComparison.Ordinal);
    }

    // F6 — an anonymous GET of a profile is redirected to login (fail-closed authorization).
    [Fact]
    public async Task F6_Anonymous_profile_redirects_to_login()
    {
        using var client = TestAuthentication.CreateClient(_factory); // anonymous — no sign-in

        using var response = await client.GetAsync($"/users/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/account/login", response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);
    }

    // F7 (coverage) — the /friends page renders accepted friends plus incoming and outgoing pending requests.
    // Exercises GetFriendsQuery (+ the nested friend projection), GetPendingRequestsQuery, and the page/partials.
    [Fact]
    public async Task F7_Friends_page_lists_accepted_friends_and_pending_requests()
    {
        using var me = await RegisterAsync("Zoe Center");
        using var buddy = await RegisterAsync("Buddy Accepted");
        using var incomingFrom = await RegisterAsync("Ingrid Incoming");
        using var outgoingTo = await RegisterAsync("Otto Outgoing");

        // Accepted friendship: me → buddy, buddy accepts.
        using var toBuddy = await SendRequestAsync(me.Client, buddy.UserId);
        Assert.Equal(HttpStatusCode.OK, toBuddy.StatusCode);
        var buddyRequest = await FriendRowAsync(me.UserId, buddy.UserId);
        using var accepted = await PostWithTokenAsync(buddy.Client, $"/friends/requests/{buddyRequest!.Id}/accept");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        // Incoming pending: Ingrid → me.
        using var incoming = await SendRequestAsync(incomingFrom.Client, me.UserId);
        Assert.Equal(HttpStatusCode.OK, incoming.StatusCode);

        // Outgoing pending: me → Otto.
        using var outgoing = await SendRequestAsync(me.Client, outgoingTo.UserId);
        Assert.Equal(HttpStatusCode.OK, outgoing.StatusCode);

        using var page = await me.Client.GetAsync("/friends");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();

        Assert.Contains("Buddy Accepted", html, StringComparison.Ordinal);     // accepted friend
        Assert.Contains("Ingrid Incoming", html, StringComparison.Ordinal);    // incoming request (Accept/Decline)
        Assert.Contains("wants to be your friend", html, StringComparison.Ordinal);
        Assert.Contains("Otto Outgoing", html, StringComparison.Ordinal);      // outgoing request (Pending)
    }

    // ---- HTTP helpers -------------------------------------------------------------------------------------

    private Task<AuthenticatedTestUser> RegisterAsync(string displayName) =>
        TestAuthentication.RegisterAndSignInAsync(_factory, displayName);

    private static async Task<HttpResponseMessage> SendRequestAsync(HttpClient client, Guid addresseeUserId)
    {
        var token = await TestAuthentication.AntiforgeryTokenAsync(client, "/discover");
        var fields = new Dictionary<string, string>
        {
            ["AddresseeUserId"] = addresseeUserId.ToString(),
            ["__RequestVerificationToken"] = token,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/friends/requests")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        request.Headers.Add("HX-Request", "true");
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostWithTokenAsync(HttpClient client, string url)
    {
        // A bodyless POST (accept/decline) carries the anti-forgery token via the RequestVerificationToken header.
        var token = await TestAuthentication.AntiforgeryTokenAsync(client, "/discover");
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("HX-Request", "true");
        return await client.SendAsync(request);
    }

    // ---- persistence helpers ------------------------------------------------------------------------------

    private async Task<Friend?> FriendRowAsync(Guid requesterId, Guid addresseeId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Friends.AsNoTracking()
            .SingleOrDefaultAsync(friend =>
                friend.RequesterId == requesterId && friend.AddresseeId == addresseeId);
    }

    private async Task<int> FriendCountForPairAsync(Guid a, Guid b)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Friends.AsNoTracking().CountAsync(friend =>
            (friend.RequesterId == a && friend.AddresseeId == b)
            || (friend.RequesterId == b && friend.AddresseeId == a));
    }

    private async Task<int> FriendCountForUserAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Friends.AsNoTracking()
            .CountAsync(friend => friend.RequesterId == userId || friend.AddresseeId == userId);
    }

    private async Task<bool> NotificationExistsAsync(
        Guid recipientUserId, Guid actorUserId, Guid targetId, NotificationType type)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Notifications.AsNoTracking().AnyAsync(notification =>
            notification.RecipientUserId == recipientUserId
            && notification.ActorUserId == actorUserId
            && notification.TargetId == targetId
            && notification.Type == type);
    }

    private async Task<Guid> SeedReviewAsync(Guid authorId, int tmdbId, string body)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

        var movie = Movie.FromTmdb(tmdbId, MediaType.Movie, $"Seed Title {tmdbId}", null, null, null, null);
        db.Movies.Add(movie);
        var review = Review.Create(authorId, movie.Id, Rating.From(8), body);
        db.Reviews.Add(review);
        await db.SaveChangesAsync();
        return review.Id;
    }

    private async Task SetProfileVisibilityAsync(Guid userId, bool isPublic)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        // The domain User set is exposed via the IAppDbContext explicit implementation (the concrete
        // context's `Users` is Identity's ApplicationUser). Set<User>() reaches the domain aggregate that
        // carries SetProfileVisibility.
        var user = await db.Set<User>().SingleAsync(candidate => candidate.Id == userId);
        user.SetProfileVisibility(isPublic);
        await db.SaveChangesAsync();
    }
}
