using System.Net;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Infrastructure.Identity;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Web.IntegrationTests.Watchlists;

/// <summary>
/// Milestone 4.4 tests for the watchlist PAGE slice, driven end-to-end through the real MVC + hand-rolled
/// <c>ISender</c> + Identity pipeline against the migrated <c>CinoraTest</c> LocalDB. They prove the §6.4 plan:
/// the page shows only the current user's entries, keyset-paginated with no overlap across the page boundary
/// (WP1); a status filter scopes the page and the count chips report the correct per-status totals (WP2); both
/// the totally-empty and the empty-filter states render as calm 200s, not errors (WP4); and the page requires
/// authentication (WP5). The page touches no TMDB, so the shared factory is used directly; entries are seeded
/// straight through the DbContext with explicit <c>AddedAtUtc</c> so the keyset ordering is deterministic. TMDB
/// ids sit in the 930_xxx range so the shared database stays isolated from the sibling suites.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class WatchlistPageTests : IAsyncLifetime
{
    // The page size the query serves (GetMyWatchlistQuery's default Take) — seed one more to force a second page.
    private const int PageSize = 24;

    private readonly CinoraWebApplicationFactory _factory;

    public WatchlistPageTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // WP1 — the page shows only MY entries, newest-added first; page 2 via the cursor of page 1's last item
    // returns strictly older items with no overlap and no gap; another user's entry never appears.
    [Fact]
    public async Task WP1_Watchlist_page_shows_only_my_entries_keyset()
    {
        const int total = PageSize + 1; // 25 → page 1 holds the 24 newest, page 2 holds the 1 oldest
        var baseTime = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Unspecified);

        using var me = await RegisterAsync("Wilma Watchlist WP1");
        var stranger = await RegisterServerUserAsync("Stranger WP1");

        // Mine: 25 entries with ascending AddedAtUtc (index 0 = oldest), one distinct title each.
        var seeded = await SeedWatchlistAsync(me.UserId, total, baseTmdbId: 930_100, baseTime);
        // A stranger's entry — must never leak into my page.
        var strangerMarker = await SeedOneAsync(
            stranger, 930_190, WatchlistStatus.Watched, baseTime.AddDays(1));

        // Page 1 via /watchlist: the 24 newest; the oldest (index 0) is NOT present, nor is the stranger's.
        using var page1 = await me.Client.GetAsync("/watchlist");
        Assert.Equal(HttpStatusCode.OK, page1.StatusCode);
        var page1Html = await page1.Content.ReadAsStringAsync();
        Assert.Contains(seeded[total - 1].Marker, page1Html, StringComparison.Ordinal); // newest, on page 1
        Assert.Contains(seeded[1].Marker, page1Html, StringComparison.Ordinal);         // index 1 = page 1's last
        Assert.DoesNotContain(seeded[0].Marker, page1Html, StringComparison.Ordinal);   // oldest, not yet
        Assert.DoesNotContain(strangerMarker, page1Html, StringComparison.Ordinal);     // owner-scoped

        // Page 2 via the cursor of page 1's last item (index 1): strictly older → only index 0, no overlap.
        var cursor = seeded[1];
        var url = $"/watchlist/page?cursorKey={Uri.EscapeDataString(cursor.AddedAt.ToString("o"))}" +
                  $"&cursorId={cursor.EntryId}";
        using var page2 = await me.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, page2.StatusCode);
        var page2Html = await page2.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", page2Html, StringComparison.OrdinalIgnoreCase); // partial only, no layout

        Assert.Contains(seeded[0].Marker, page2Html, StringComparison.Ordinal);          // the next older item appears
        Assert.DoesNotContain(seeded[1].Marker, page2Html, StringComparison.Ordinal);    // no overlap with page 1
        Assert.DoesNotContain(seeded[total - 1].Marker, page2Html, StringComparison.Ordinal); // no overlap with page 1
    }

    // WP2 — ?status=Watched scopes the grid to Watched entries; the count chips report the per-status totals.
    [Fact]
    public async Task WP2_Filter_by_status_scopes_the_page_and_counts()
    {
        var baseTime = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Unspecified);
        using var me = await RegisterAsync("Fiona Filter WP2");

        // Distinct per-status counts (3 Watched, 2 PlanToWatch, 1 Watching → All 6) so each chip number is unique.
        var watchedA = await SeedOneAsync(me.UserId, 930_300, WatchlistStatus.Watched, baseTime.AddMinutes(6));
        var watchedB = await SeedOneAsync(me.UserId, 930_301, WatchlistStatus.Watched, baseTime.AddMinutes(5));
        var watchedC = await SeedOneAsync(me.UserId, 930_302, WatchlistStatus.Watched, baseTime.AddMinutes(4));
        var planA = await SeedOneAsync(me.UserId, 930_303, WatchlistStatus.PlanToWatch, baseTime.AddMinutes(3));
        var planB = await SeedOneAsync(me.UserId, 930_304, WatchlistStatus.PlanToWatch, baseTime.AddMinutes(2));
        var watching = await SeedOneAsync(me.UserId, 930_305, WatchlistStatus.Watching, baseTime.AddMinutes(1));

        // Filtered view: only the Watched titles; the Plan / Watching titles are absent.
        using var filtered = await me.Client.GetAsync("/watchlist?status=Watched");
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        var html = await filtered.Content.ReadAsStringAsync();

        Assert.Contains(watchedA, html, StringComparison.Ordinal);
        Assert.Contains(watchedB, html, StringComparison.Ordinal);
        Assert.Contains(watchedC, html, StringComparison.Ordinal);
        Assert.DoesNotContain(planA, html, StringComparison.Ordinal);
        Assert.DoesNotContain(planB, html, StringComparison.Ordinal);
        Assert.DoesNotContain(watching, html, StringComparison.Ordinal);

        // The count chips report the correct per-status totals (and the "All" total) regardless of the filter.
        Assert.Contains("data-watchlist-count=\"All\">6</span>", html, StringComparison.Ordinal);
        Assert.Contains("data-watchlist-count=\"PlanToWatch\">2</span>", html, StringComparison.Ordinal);
        Assert.Contains("data-watchlist-count=\"Watching\">1</span>", html, StringComparison.Ordinal);
        Assert.Contains("data-watchlist-count=\"Watched\">3</span>", html, StringComparison.Ordinal);
    }

    // WP4 — no entries → the totally-empty "start your watchlist" state; entries but none in the active filter →
    // the quiet empty-filter state. Both are calm 200s (markers decoupled from copy), never errors.
    [Fact]
    public async Task WP4_Empty_and_empty_filter_states_render()
    {
        // (a) A freshly-registered user with zero entries sees the totally-empty state.
        using var empty = await RegisterAsync("Ella Empty WP4");
        using var emptyResponse = await empty.Client.GetAsync("/watchlist");
        Assert.Equal(HttpStatusCode.OK, emptyResponse.StatusCode);
        var emptyHtml = await emptyResponse.Content.ReadAsStringAsync();
        Assert.Contains("data-watchlist-empty=\"none\"", emptyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("data-watchlist-empty=\"filter\"", emptyHtml, StringComparison.Ordinal);

        // (b) A user with a PlanToWatch entry, filtered to Watched, sees the empty-filter state (not totally-empty).
        using var some = await RegisterAsync("Fred Filtered WP4");
        await SeedOneAsync(some.UserId, 930_400, WatchlistStatus.PlanToWatch,
            new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Unspecified));

        using var filterResponse = await some.Client.GetAsync("/watchlist?status=Watched");
        Assert.Equal(HttpStatusCode.OK, filterResponse.StatusCode);
        var filterHtml = await filterResponse.Content.ReadAsStringAsync();
        Assert.Contains("data-watchlist-empty=\"filter\"", filterHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("data-watchlist-empty=\"none\"", filterHtml, StringComparison.Ordinal);
    }

    // WP5 — an anonymous GET of /watchlist is redirected to login (fail-closed authorization).
    [Fact]
    public async Task WP5_Watchlist_page_requires_auth()
    {
        using var client = TestAuthentication.CreateClient(_factory); // anonymous — no sign-in

        using var response = await client.GetAsync("/watchlist");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/account/login",
            response.Headers.Location?.OriginalString ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    // ---- seed helpers -------------------------------------------------------------------------------------

    private Task<AuthenticatedTestUser> RegisterAsync(string displayName) =>
        TestAuthentication.RegisterAndSignInAsync(_factory, displayName);

    // Creates a user through the atomic registration seam without an authenticated client (a stranger the test
    // seeds entries for but never posts as).
    private async Task<Guid> RegisterServerUserAsync(string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var registration = scope.ServiceProvider.GetRequiredService<IUserRegistrationService>();
        var result = await registration.RegisterAsync(
            $"wlpage-{Guid.NewGuid():N}@cinora.test", "Test1234!", $"{displayName} {Guid.NewGuid():N}", CancellationToken.None);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Seed registration for '{displayName}' failed.");
        }

        return result.UserId;
    }

    // Seeds one user's watchlist entries across `count` distinct titles with explicit ascending AddedAtUtc
    // (index 0 = oldest), in a single unit of work. Each title carries a GUID marker so the test can assert its
    // presence in the rendered HTML. Returns the entries in ascending-time order for keyset assertions.
    private async Task<IReadOnlyList<SeededEntry>> SeedWatchlistAsync(
        Guid userId, int count, int baseTmdbId, DateTime baseTime)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

        var seeded = new List<SeededEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var marker = $"wl-item-{Guid.NewGuid():N}";
            var movie = Movie.FromTmdb(baseTmdbId + i, MediaType.Movie, marker, null, null, null, null);
            db.Movies.Add(movie);

            var entry = Watchlist.Add(userId, movie.Id, WatchlistStatus.PlanToWatch);
            db.Watchlists.Add(entry);

            var addedAt = baseTime.AddMinutes(i);
            db.Entry(entry).Property(nameof(Watchlist.AddedAtUtc)).CurrentValue = addedAt;

            seeded.Add(new SeededEntry(entry.Id, addedAt, marker));
        }

        await db.SaveChangesAsync();
        return seeded;
    }

    // Seeds a single title + one entry for a user at an explicit AddedAtUtc; returns the title's GUID marker so
    // the test can assert its presence/absence in the rendered HTML.
    private async Task<string> SeedOneAsync(Guid userId, int tmdbId, WatchlistStatus status, DateTime addedAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

        var marker = $"wl-item-{Guid.NewGuid():N}";
        var movie = Movie.FromTmdb(tmdbId, MediaType.Movie, marker, null, null, null, null);
        db.Movies.Add(movie);

        var entry = Watchlist.Add(userId, movie.Id, status);
        db.Watchlists.Add(entry);
        db.Entry(entry).Property(nameof(Watchlist.AddedAtUtc)).CurrentValue = addedAt;

        await db.SaveChangesAsync();
        return marker;
    }

    private sealed record SeededEntry(Guid EntryId, DateTime AddedAt, string Marker);
}
