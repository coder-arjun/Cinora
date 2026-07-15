using Cinora.Application.Common.Interfaces;
using Cinora.Application.Features.Profiles;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Infrastructure.Identity;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Web.IntegrationTests.Profiles;

/// <summary>
/// Milestone 4.5 tests for the privacy-shaped watchlist-highlights read (§7, plan PH1–PH3). They dispatch the
/// real <see cref="GetProfileQueryHandler"/> against the migrated <c>CinoraTest</c> LocalDB with a fixed
/// <see cref="ICurrentUser"/> viewer, so the assertion is on the projected <see cref="ProfileVm"/> the view
/// receives — independent of markup — exactly as <c>AvatarProjectionTests</c>. This is what proves the read is
/// gated at the DATA layer: a limited (private, non-friend) viewer's VM carries an EMPTY highlights list and
/// <see cref="ProfileVm.CanViewWatchlist"/> is <c>false</c> (PH1), the visibility tracks the §7.3 tier exactly
/// (PH2), and the projection is Watched-only, newest-first, capped at <see cref="GetProfileQueryHandler.HighlightsCount"/>
/// (PH3). TMDB ids sit in the 960_xxx range so the shared database stays isolated from the sibling suites.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class WatchlistHighlightsTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public WatchlistHighlightsTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // PH1 — a signed-in non-friend of a PRIVATE owner sees NO highlights and the query never fetched them (the
    // projected VM's list is empty AND CanViewWatchlist is false, even though the owner HAS Watched entries);
    // flipping the owner public makes the same non-friend see them.
    [Fact]
    public async Task PH1_Private_profile_hides_watchlist_highlights_from_non_friend()
    {
        var baseTime = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var owner = await RegisterServerUserAsync("Highlights Owner PH1");
        var viewer = await RegisterServerUserAsync("Highlights Viewer PH1"); // a non-friend

        var watchedA = await SeedEntryAsync(owner, 960_010, WatchlistStatus.Watched, baseTime.AddMinutes(2));
        var watchedB = await SeedEntryAsync(owner, 960_011, WatchlistStatus.Watched, baseTime.AddMinutes(1));

        // Private: the non-friend viewer receives an EMPTY highlights list — the read never ran for this tier.
        await SetProfileVisibilityAsync(owner, isPublic: false);
        var privateVm = await ProfileAsAsync(viewer, owner);
        Assert.False(privateVm.CanViewWatchlist);
        Assert.Empty(privateVm.WatchlistHighlights);

        // Public: the same non-friend now sees the owner's Watched titles.
        await SetProfileVisibilityAsync(owner, isPublic: true);
        var publicVm = await ProfileAsAsync(viewer, owner);
        Assert.True(publicVm.CanViewWatchlist);
        Assert.Contains(publicVm.WatchlistHighlights, h => h.TmdbId == watchedA);
        Assert.Contains(publicVm.WatchlistHighlights, h => h.TmdbId == watchedB);
    }

    // PH2 — the highlight visibility tracks the §7.3 tier EXACTLY: owner (self), accepted friend, and a public
    // non-friend all see them (CanViewWatchlist == CanViewReviews == true); a limited (private, non-friend) view
    // is absent (both flags false). Proven against ONE owner with the same seeded Watched titles.
    [Fact]
    public async Task PH2_Highlights_reuse_the_privacy_tier()
    {
        var baseTime = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Unspecified);
        var owner = await RegisterServerUserAsync("Tier Owner PH2");
        var friend = await RegisterServerUserAsync("Tier Friend PH2");
        var stranger = await RegisterServerUserAsync("Tier Stranger PH2");

        var watched = await SeedEntryAsync(owner, 960_020, WatchlistStatus.Watched, baseTime.AddMinutes(1));
        await MakeAcceptedFriendsAsync(owner, friend);

        // Owner is PRIVATE: the owner and the accepted friend still see highlights; the stranger (limited) does not.
        await SetProfileVisibilityAsync(owner, isPublic: false);

        var ownerVm = await ProfileAsAsync(owner, owner);       // owner tier
        AssertTierVisible(ownerVm, watched);

        var friendVm = await ProfileAsAsync(friend, owner);     // accepted-friend tier
        AssertTierVisible(friendVm, watched);

        var strangerPrivateVm = await ProfileAsAsync(stranger, owner); // limited tier
        Assert.Equal(strangerPrivateVm.CanViewReviews, strangerPrivateVm.CanViewWatchlist);
        Assert.False(strangerPrivateVm.CanViewWatchlist);
        Assert.Empty(strangerPrivateVm.WatchlistHighlights);

        // Owner flips PUBLIC: the same stranger now sits in the public tier and sees highlights.
        await SetProfileVisibilityAsync(owner, isPublic: true);
        var strangerPublicVm = await ProfileAsAsync(stranger, owner); // public non-friend tier
        AssertTierVisible(strangerPublicVm, watched);
    }

    // PH3 (§7.1) — the projection is Watched-only, newest-added first, and capped at HighlightsCount: 10 Watched
    // entries (ascending AddedAtUtc) plus a newer PlanToWatch and a newer Watching entry yield exactly the 8
    // newest Watched titles, newest-first, with the non-Watched titles absent despite being newer.
    [Fact]
    public async Task PH3_Highlights_are_watched_only_newest_first_and_capped()
    {
        const int watchedCount = 10; // > HighlightsCount (8) → the 2 oldest are dropped
        var baseTime = new DateTime(2026, 4, 3, 0, 0, 0, DateTimeKind.Unspecified);
        var owner = await RegisterServerUserAsync("Cap Owner PH3");

        // 10 Watched titles: index 0 = oldest, index 9 = newest. tmdbId encodes the index for ordered assertions.
        var watched = new int[watchedCount];
        for (var i = 0; i < watchedCount; i++)
        {
            watched[i] = await SeedEntryAsync(
                owner, 960_100 + i, WatchlistStatus.Watched, baseTime.AddMinutes(i));
        }

        // Non-Watched titles, both NEWER than every Watched entry — they must still be excluded by the status filter.
        var plan = await SeedEntryAsync(owner, 960_130, WatchlistStatus.PlanToWatch, baseTime.AddMinutes(100));
        var watching = await SeedEntryAsync(owner, 960_131, WatchlistStatus.Watching, baseTime.AddMinutes(101));

        var vm = await ProfileAsAsync(owner, owner); // owner tier — the simplest visible tier

        // Capped at HighlightsCount.
        Assert.Equal(GetProfileQueryHandler.HighlightsCount, vm.WatchlistHighlights.Count);

        // Watched-only: neither non-Watched title appears, even though both are newer than every Watched entry.
        Assert.DoesNotContain(vm.WatchlistHighlights, h => h.TmdbId == plan);
        Assert.DoesNotContain(vm.WatchlistHighlights, h => h.TmdbId == watching);

        // The 8 newest Watched, newest-first: indices 9..2 (the two oldest, indices 0 and 1, are dropped).
        var expectedOrder = new[]
        {
            watched[9], watched[8], watched[7], watched[6],
            watched[5], watched[4], watched[3], watched[2],
        };
        Assert.Equal(expectedOrder, vm.WatchlistHighlights.Select(h => h.TmdbId).ToArray());
        Assert.DoesNotContain(vm.WatchlistHighlights, h => h.TmdbId == watched[0]); // oldest dropped
        Assert.DoesNotContain(vm.WatchlistHighlights, h => h.TmdbId == watched[1]); // 2nd-oldest dropped
    }

    // ---- assertions ---------------------------------------------------------------------------------------

    // A visible tier: CanViewWatchlist mirrors CanViewReviews (both true) and the seeded Watched title is present.
    private static void AssertTierVisible(ProfileVm vm, int seededTmdbId)
    {
        Assert.Equal(vm.CanViewReviews, vm.CanViewWatchlist);
        Assert.True(vm.CanViewWatchlist);
        Assert.Contains(vm.WatchlistHighlights, h => h.TmdbId == seededTmdbId);
    }

    // ---- dispatch -----------------------------------------------------------------------------------------

    // Dispatches the real GetProfileQuery handler for `owner` as seen by `viewer`, returning the projected VM.
    private async Task<ProfileVm> ProfileAsAsync(Guid viewer, Guid owner)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var handler = new GetProfileQueryHandler(db, new StubCurrentUser(viewer));
        return await handler.Handle(new GetProfileQuery(owner), CancellationToken.None);
    }

    // ---- seed helpers -------------------------------------------------------------------------------------

    // Creates a user through the atomic registration seam (identity + domain rows share one PK) without an
    // authenticated client — this suite asserts VMs directly and never posts as these users.
    private async Task<Guid> RegisterServerUserAsync(string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var registration = scope.ServiceProvider.GetRequiredService<IUserRegistrationService>();
        var result = await registration.RegisterAsync(
            $"highlight-{Guid.NewGuid():N}@cinora.test", "Test1234!", $"{displayName} {Guid.NewGuid():N}", CancellationToken.None);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Seed registration for '{displayName}' failed.");
        }

        return result.UserId;
    }

    // Seeds one title + one watchlist entry for a user at an explicit status/AddedAtUtc; returns the title's TMDB
    // id so the test can assert its presence/absence/order on the projected VM. AddedAtUtc is set via the entry's
    // property (the setter is private) so the newest-first ordering is deterministic.
    private async Task<int> SeedEntryAsync(Guid userId, int tmdbId, WatchlistStatus status, DateTime addedAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

        var movie = Movie.FromTmdb(tmdbId, MediaType.Movie, $"Highlight Title {tmdbId}", null, null, null, null);
        db.Movies.Add(movie);

        var entry = Watchlist.Add(userId, movie.Id, status);
        db.Watchlists.Add(entry);
        db.Entry(entry).Property(nameof(Watchlist.AddedAtUtc)).CurrentValue = addedAt;

        await db.SaveChangesAsync();
        return tmdbId;
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

    private async Task SetProfileVisibilityAsync(Guid userId, bool isPublic)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        // Set<User>() reaches the domain aggregate (the context's `Users` is Identity's ApplicationUser).
        var user = await db.Set<User>().SingleAsync(candidate => candidate.Id == userId);
        user.SetProfileVisibility(isPublic);
        await db.SaveChangesAsync();
    }

    // A fixed, always-authenticated ICurrentUser — the viewer the handler projects the profile for (hand-rolled
    // double, no mocking package, consistent with the rest of the suite).
    private sealed class StubCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;

        public bool IsAuthenticated => true;

        public Guid GetRequiredUserId() => userId;
    }
}
