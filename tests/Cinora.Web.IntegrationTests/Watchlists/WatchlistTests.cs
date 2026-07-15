using System.Globalization;
using System.Net;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Tmdb;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

namespace Cinora.Web.IntegrationTests.Watchlists;

/// <summary>
/// Milestone 4.3 mock-first tests for the watchlist status control, driven end-to-end through the real MVC +
/// hand-rolled <c>ISender</c> + Identity pipeline against the migrated <c>CinoraTest</c> LocalDB with a
/// <see cref="FakeTmdbClient"/> replacing the network. They prove the §5.6 plan: set-from-a-surface first-touch
/// persists the title (ADR 0008) then upserts (W1), remove is idempotent (W2), the write requires auth + the
/// anti-forgery token (W4), the actor is server-resolved and a spoofed <c>UserId</c> is ignored (W5), and a
/// Watched response offers the review nudge (W6). The batch status map (W3) is a cheap unit test in
/// <c>Cinora.Application.Tests</c>. TMDB ids sit in the 920_xxx range so the shared database stays isolated.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class WatchlistTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public WatchlistTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // W1 — a set from any surface first-touch persists the title (ADR 0008) and creates the entry; a second
    // set updates the SAME row (no duplicate; the unique (UserId, MovieId) index holds).
    [Fact]
    public async Task W1_Set_status_from_a_card_persists_after_ensuring_the_title()
    {
        const int tmdbId = 920_001;
        using var factory = CreateFactoryWith(FakeWithMovie(tmdbId));
        using var user = await TestAuthentication.RegisterAndSignInAsync(factory, "Wanda Watcher");

        var token1 = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/discover");
        using var first = await PostSetAsync(user.Client, token1, tmdbId, MediaType.Movie, WatchlistStatus.PlanToWatch);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", firstBody, StringComparison.OrdinalIgnoreCase); // the _WatchlistControl partial

        // ADR 0008 first-touch: the Movie row now exists, and the entry was created as PlanToWatch.
        Assert.True(await MovieExistsAsync(factory, tmdbId));
        var created = await WatchlistEntryAsync(factory, tmdbId, user.UserId);
        Assert.NotNull(created);
        Assert.Equal(WatchlistStatus.PlanToWatch, created!.Status);

        // A second set to Watched updates the SAME row rather than duplicating it.
        var token2 = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/discover");
        using var second = await PostSetAsync(user.Client, token2, tmdbId, MediaType.Movie, WatchlistStatus.Watched);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Equal(1, await WatchlistCountAsync(factory, tmdbId, user.UserId)); // no duplicate row
        var updated = await WatchlistEntryAsync(factory, tmdbId, user.UserId);
        Assert.Equal(WatchlistStatus.Watched, updated!.Status);
        Assert.Equal(created.Id, updated.Id);                                     // same row, updated in place
    }

    // W2 — DELETE removes the entry and returns the "Add" control; a second DELETE is an idempotent no-op 200.
    [Fact]
    public async Task W2_Remove_is_idempotent()
    {
        const int tmdbId = 920_002;
        using var factory = CreateFactoryWith(FakeWithMovie(tmdbId));
        using var user = await TestAuthentication.RegisterAndSignInAsync(factory, "Rex Remover");

        // Add first (persists the title + creates the entry).
        var addToken = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/discover");
        using var add = await PostSetAsync(user.Client, addToken, tmdbId, MediaType.Movie, WatchlistStatus.PlanToWatch);
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        Assert.NotNull(await WatchlistEntryAsync(factory, tmdbId, user.UserId));

        var delToken1 = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/discover");
        using var delete1 = await DeleteWatchlistAsync(user.Client, delToken1, tmdbId, MediaType.Movie);
        Assert.Equal(HttpStatusCode.OK, delete1.StatusCode);
        Assert.Null(await WatchlistEntryAsync(factory, tmdbId, user.UserId));      // removed
        var delete1Body = await delete1.Content.ReadAsStringAsync();
        Assert.Contains("Add to watchlist", delete1Body, StringComparison.Ordinal); // control back to "Add"

        // A second DELETE with nothing to remove is a calm no-op success (idempotent), not an error.
        var delToken2 = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/discover");
        using var delete2 = await DeleteWatchlistAsync(user.Client, delToken2, tmdbId, MediaType.Movie);
        Assert.Equal(HttpStatusCode.OK, delete2.StatusCode);
        Assert.Null(await WatchlistEntryAsync(factory, tmdbId, user.UserId));
    }

    // W4 — the write is fail-closed and anti-forgery-guarded: anonymous → 302 login; authed but missing the
    // token → 400.
    [Fact]
    public async Task W4_Watchlist_write_requires_auth_and_token()
    {
        // Anonymous POST → 302 to /account/login (the global fallback authorization is fail-closed).
        using var anon = TestAuthentication.CreateClient(_factory);
        using var anonPost = await anon.PostAsync(
            "/watchlist",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["TmdbId"] = "920003",
                ["Media"] = nameof(MediaType.Movie),
                ["Status"] = nameof(WatchlistStatus.PlanToWatch),
            }));
        Assert.Equal(HttpStatusCode.Redirect, anonPost.StatusCode);
        Assert.Contains(
            "/account/login",
            anonPost.Headers.Location?.OriginalString ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        // Authenticated but WITHOUT the anti-forgery token → 400 (the global AutoValidateAntiforgeryToken).
        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "Nadia NoToken");
        using var noToken = await user.Client.PostAsync(
            "/watchlist",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["TmdbId"] = "920003",
                ["Media"] = nameof(MediaType.Movie),
                ["Status"] = nameof(WatchlistStatus.PlanToWatch),
            }));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
    }

    // W5 — the entry is stamped with the SERVER-resolved user; a hidden UserId=other field is ignored (ADR 0009).
    [Fact]
    public async Task W5_Set_status_actor_is_server_resolved()
    {
        const int tmdbId = 920_005;
        using var factory = CreateFactoryWith(FakeWithMovie(tmdbId));
        using var user = await TestAuthentication.RegisterAndSignInAsync(factory, "Sam Server");
        var spoofedUserId = Guid.NewGuid();

        var token = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/discover");
        using var response = await PostSetAsync(
            user.Client, token, tmdbId, MediaType.Movie, WatchlistStatus.Watching,
            extraFields: new Dictionary<string, string> { ["UserId"] = spoofedUserId.ToString() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = await WatchlistEntryAsync(factory, tmdbId, user.UserId);
        Assert.NotNull(entry);
        Assert.Equal(user.UserId, entry!.UserId);      // the actor came from the auth cookie, server-side
        Assert.NotEqual(spoofedUserId, entry.UserId);  // the bound-in UserId field was never honoured
        Assert.Equal(WatchlistStatus.Watching, entry.Status);
    }

    // W6 — setting Watched on the Details control returns markup that links the in-page #my-review write region.
    [Fact]
    public async Task W6_Watched_response_offers_the_review_nudge()
    {
        const int tmdbId = 920_006;
        using var factory = CreateFactoryWith(FakeWithMovie(tmdbId));
        using var user = await TestAuthentication.RegisterAndSignInAsync(factory, "Vic Viewer");

        var token = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/discover");
        // The Details control posts with OnDetails=true; a Watched result surfaces the review nudge (§5.4).
        using var response = await PostSetAsync(
            user.Client, token, tmdbId, MediaType.Movie, WatchlistStatus.Watched, onDetails: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase); // the _WatchlistControl partial
        Assert.Contains("Watched", body, StringComparison.Ordinal);               // reflects the new status
        Assert.Contains("#my-review", body, StringComparison.Ordinal);            // nudge links the review region
        Assert.Contains("Write your review", body, StringComparison.Ordinal);
    }

    // ---- HTTP helpers -------------------------------------------------------------------------------------

    private static async Task<HttpResponseMessage> PostSetAsync(
        HttpClient client,
        string token,
        int tmdbId,
        MediaType media,
        WatchlistStatus status,
        bool onDetails = false,
        IReadOnlyDictionary<string, string>? extraFields = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["TmdbId"] = tmdbId.ToString(CultureInfo.InvariantCulture),
            ["Media"] = media.ToString(),
            ["Status"] = status.ToString(),
            ["OnDetails"] = onDetails ? "true" : "false",
            ["__RequestVerificationToken"] = token,
        };
        if (extraFields is not null)
        {
            foreach (var (key, value) in extraFields)
            {
                fields[key] = value;
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/watchlist")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        request.Headers.Add("HX-Request", "true"); // ask for the _WatchlistControl partial rather than a redirect
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> DeleteWatchlistAsync(
        HttpClient client, string token, int tmdbId, MediaType media, bool onDetails = false)
    {
        // hx-delete carries its params in the query string and the anti-forgery token in the header.
        var url = $"/watchlist?tmdbId={tmdbId}&media={media}&onDetails={(onDetails ? "true" : "false")}";
        var request = new HttpRequestMessage(HttpMethod.Delete, url);
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
            Title = $"Watchable Title {tmdbId}",
            Overview = "A canned overview.",
            Genres = [],
        };
        return fake;
    }

    private static async Task<bool> MovieExistsAsync(WebApplicationFactory<Program> factory, int tmdbId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Movies.AsNoTracking()
            .AnyAsync(movie => movie.TmdbId == tmdbId && movie.MediaType == MediaType.Movie);
    }

    private static async Task<Watchlist?> WatchlistEntryAsync(
        WebApplicationFactory<Program> factory, int tmdbId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var movieId = await MovieIdAsync(db, tmdbId);
        return movieId is { } id
            ? await db.Watchlists.AsNoTracking()
                .FirstOrDefaultAsync(entry => entry.MovieId == id && entry.UserId == userId)
            : null;
    }

    private static async Task<int> WatchlistCountAsync(
        WebApplicationFactory<Program> factory, int tmdbId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var movieId = await MovieIdAsync(db, tmdbId);
        return movieId is { } id
            ? await db.Watchlists.AsNoTracking().CountAsync(entry => entry.MovieId == id && entry.UserId == userId)
            : 0;
    }

    private static Task<Guid?> MovieIdAsync(CinoraDbContext db, int tmdbId) =>
        db.Movies.AsNoTracking()
            .Where(movie => movie.TmdbId == tmdbId && movie.MediaType == MediaType.Movie)
            .Select(movie => (Guid?)movie.Id)
            .FirstOrDefaultAsync();

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
}
