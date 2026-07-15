using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Tmdb;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Domain.ValueObjects;
using Cinora.Infrastructure.Identity;
using Cinora.Infrastructure.Jobs;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

namespace Cinora.Web.IntegrationTests.Recommendations;

/// <summary>
/// Milestone 5.3 end-to-end test for <c>RecommendationPrecomputeJob.RunAsync</c>, driven DIRECTLY (bypassing
/// Hangfire's scheduler + storage, so no Hangfire schema is needed) over the migrated <c>CinoraTest</c> LocalDB.
/// The two external boundaries are faked (a <see cref="FakeTmdbClient"/> and a <see cref="FakeRecommendationEngine"/>);
/// everything else — the real <see cref="ISender"/>, the taste/candidate seams, the hallucination guard, history
/// persistence, and the served cache — runs for real. It proves the active-user selection + skip-unchanged +
/// per-user dispatch + idempotency contract: a STALE user (activity newer than any history) gets exactly one new
/// history row + a warmed cache; a FRESH user (a history row newer than their latest activity) is skipped; a user
/// with NO activity is never selected; an OLD user whose only activity predates the lookback window is excluded by
/// the scan; and a re-run right after produces no duplicate rows (the stale user is now fresh). The FRESH and OLD
/// users are each given an AVAILABLE candidate seed on purpose, so a failure-to-skip / failure-to-exclude would
/// write a new history row and bump the engine call count — making those assertions independently falsifying.
/// TMDB ids sit in the 974_xxx range to stay isolated from the sibling suites.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class RecommendationPrecomputeJobTests : IAsyncLifetime
{
    private const int StaleSeedTmdbId = 974_001;
    private const int StaleRecommendedTmdbId = 974_010;
    private const int StaleGenreTmdbId = 974_028;
    private const int FreshMovieTmdbId = 974_003;
    private const int FreshRecommendedTmdbId = 974_013;
    private const int OldSeedTmdbId = 974_005;
    private const int OldRecommendedTmdbId = 974_015;

    private readonly CinoraWebApplicationFactory _factory;

    public RecommendationPrecomputeJobTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RunAsync_generates_for_stale_users_skips_fresh_and_idle_users_and_is_idempotent()
    {
        var staleUser = await RegisterServerUserAsync("Stella Stale 5.3");
        var freshUser = await RegisterServerUserAsync("Fred Fresh 5.3");
        var idleUser = await RegisterServerUserAsync("Ida Idle 5.3");
        var oldUser = await RegisterServerUserAsync("Oscar Old 5.3");

        // STALE: review activity + a candidate seed, and NO history → the job must regenerate.
        await SeedStaleUserAsync(staleUser);

        // FRESH: review activity AND a pre-seeded history row dated AFTER that activity → the job must skip.
        await SeedFreshUserAsync(freshUser);

        // IDLE (idleUser): no reviews, no watchlist, no history → the job must never select them.

        // OLD: review activity dated BEFORE the lookback cutoff, and NO history → the lookback scan must exclude
        // them, even though a candidate seed is available (so a broken filter would visibly regenerate).
        await SeedOldUserAsync(oldUser);

        // The fake fan-out returns ONE unseen candidate PER seed. The stale seed is expected to generate; the fresh
        // and old seeds are wired ON PURPOSE so a failure-to-skip (fresh) or failure-to-exclude (old) would write a
        // NEW history row + bump the engine call count — making those assertions independently falsifying. Leaving
        // popular/discover empty still means unrelated stale users from sibling suites yield no candidates.
        var fakeTmdb = new FakeTmdbClient();
        fakeTmdb.Recommendations[(MediaType.Movie, StaleSeedTmdbId)] =
        [
            Summary(StaleRecommendedTmdbId, "Stale Recommended"),
        ];
        fakeTmdb.Recommendations[(MediaType.Movie, FreshMovieTmdbId)] =
        [
            Summary(FreshRecommendedTmdbId, "Fresh Recommended"),
        ];
        fakeTmdb.Recommendations[(MediaType.Movie, OldSeedTmdbId)] =
        [
            Summary(OldRecommendedTmdbId, "Old Recommended"),
        ];
        var fakeEngine = new FakeRecommendationEngine();

        using var factory = CreateFactoryWith(fakeTmdb, fakeEngine);
        var job = factory.Services.GetRequiredService<RecommendationPrecomputeJob>();

        // The fresh user's pre-run history — asserted untouched by both runs.
        var freshBefore = await HistoryRowsAsync(factory, freshUser);
        var seededFreshRow = Assert.Single(freshBefore);

        // ---- First run ----
        await job.RunAsync(CancellationToken.None);

        // STALE → exactly one AI history row (written by the fake engine's generation) + a warmed served cache.
        var staleRows = await HistoryRowsAsync(factory, staleUser);
        var staleRow = Assert.Single(staleRows);
        Assert.Equal("fake-integration", staleRow.Model);
        Assert.NotNull(await ServedCacheBytesAsync(factory, staleUser));

        // FRESH → skipped: still exactly the pre-seeded row (same id, same marker model), never regenerated.
        var freshAfter = await HistoryRowsAsync(factory, freshUser);
        var freshRow = Assert.Single(freshAfter);
        Assert.Equal(seededFreshRow.Id, freshRow.Id);
        Assert.Equal("seeded-fresh", freshRow.Model);

        // IDLE → never selected (no activity), so no generation and no history row.
        Assert.Empty(await HistoryRowsAsync(factory, idleUser));

        // OLD → excluded by the lookback window (activity older than PrecomputeLookbackDays), so no history row —
        // even though a candidate seed was available, proving the exclusion (not a missing candidate) is the cause.
        Assert.Empty(await HistoryRowsAsync(factory, oldUser));

        // ---- Re-run (idempotency) ----
        await job.RunAsync(CancellationToken.None);

        // The stale user is now FRESH (their history row is newer than their activity) → skipped → still exactly
        // one row (the first run's), no duplicate.
        var staleRowsAfterRerun = await HistoryRowsAsync(factory, staleUser);
        var staleRowAfterRerun = Assert.Single(staleRowsAfterRerun);
        Assert.Equal(staleRow.Id, staleRowAfterRerun.Id);

        // The fresh, idle, and old users are still unchanged after the re-run.
        Assert.Single(await HistoryRowsAsync(factory, freshUser));
        Assert.Empty(await HistoryRowsAsync(factory, idleUser));
        Assert.Empty(await HistoryRowsAsync(factory, oldUser));

        // The engine ran exactly once across BOTH runs — only the single stale-user generation on the first run.
        // (If the fresh user were mis-classified stale, or the old user not excluded by the lookback, their wired
        // candidate seeds would each add a generation here — so this equality also guards those two paths.)
        Assert.Equal(1, fakeEngine.CallCount);
    }

    // STALE seed: a reviewed movie (linked to a favorite genre → seeds the "more like this" fan-out) whose TMDB id
    // is the fake's recommendation seed, so generation yields a grounded candidate → one history row.
    private async Task SeedStaleUserAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

        var genre = Genre.Create(StaleGenreTmdbId, "Precompute Action 974");
        db.Genres.Add(genre);

        var reviewed = Movie.FromTmdb(
            StaleSeedTmdbId, MediaType.Movie, "Stale Seed 974", null, new DateTime(2014, 11, 7), "/s.jpg", null);
        db.Movies.Add(reviewed);
        db.MovieGenres.Add(MovieGenre.Link(reviewed.Id, genre.Id));
        db.Reviews.Add(Review.Create(userId, reviewed.Id, Rating.From(9), "A grounded stale seed review."));

        await db.SaveChangesAsync();
    }

    // FRESH seed: review activity PLUS an AIRecommendationHistory row whose GeneratedAtUtc is forced an hour into
    // the future (via the change tracker, since the timestamp is set internally to UtcNow on Create), so it is
    // unambiguously newer than the activity → the skip-unchanged check treats the user as fresh.
    private async Task SeedFreshUserAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

        var reviewed = Movie.FromTmdb(
            FreshMovieTmdbId, MediaType.Movie, "Fresh Movie 974", null, new DateTime(2010, 1, 1), "/f.jpg", null);
        db.Movies.Add(reviewed);
        db.Reviews.Add(Review.Create(userId, reviewed.Id, Rating.From(7), "A fresh-user review."));

        var history = AIRecommendationHistory.Create(
            userId, "seeded-fresh", "seeded-input", "seeded-output", promptTokens: 1, completionTokens: 1);
        db.AIRecommendationHistories.Add(history);

        // Force the generation instant clearly AFTER the review activity so the user is unambiguously "fresh".
        db.Entry(history).Property(row => row.GeneratedAtUtc).CurrentValue = DateTime.UtcNow.AddHours(1);

        await db.SaveChangesAsync();
    }

    // OLD seed: a reviewed movie whose TMDB id is the fake's recommendation seed (so a grounded candidate IS
    // available) but whose review activity is forced clearly BEFORE the lookback cutoff, and NO history. The job's
    // lookback-filtered activity scan must therefore drop the user entirely → no generation, no history row. If the
    // filter were missing, the available candidate would produce a history row + an engine call, failing the test.
    private async Task SeedOldUserAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

        var reviewed = Movie.FromTmdb(
            OldSeedTmdbId, MediaType.Movie, "Old Seed 974", null, new DateTime(2001, 1, 1), "/o.jpg", null);
        db.Movies.Add(reviewed);

        var review = Review.Create(userId, reviewed.Id, Rating.From(8), "An old-but-grounded review.");
        db.Reviews.Add(review);

        // Force the review activity 90 days back — well before the 45-day PrecomputeLookbackDays default — via the
        // change tracker (Review.Create stamps CreatedAtUtc to UtcNow). UpdatedAtUtc stays null, so BOTH lookback
        // predicates (CreatedAtUtc >= cutoff, UpdatedAtUtc >= cutoff) are false and the row is excluded from the scan.
        db.Entry(review).Property(row => row.CreatedAtUtc).CurrentValue = DateTime.UtcNow.AddDays(-90);

        await db.SaveChangesAsync();
    }

    private async Task<Guid> RegisterServerUserAsync(string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var registration = scope.ServiceProvider.GetRequiredService<IUserRegistrationService>();
        var result = await registration.RegisterAsync(
            $"precompute-{Guid.NewGuid():N}@cinora.test", "Test1234!", $"{displayName} {Guid.NewGuid():N}", CancellationToken.None);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Seed registration for '{displayName}' failed.");
        }

        return result.UserId;
    }

    private static async Task<List<AIRecommendationHistory>> HistoryRowsAsync(
        WebApplicationFactory<Program> factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Set<AIRecommendationHistory>().AsNoTracking()
            .Where(history => history.UserId == userId)
            .OrderBy(history => history.GeneratedAtUtc)
            .ToListAsync();
    }

    // Reads the raw served-cache bytes for the user via the same singleton IDistributedCache the adapter writes,
    // using the ServedRecommendationCache key convention (cinora:ai:recs:{userId}).
    private static async Task<byte[]?> ServedCacheBytesAsync(WebApplicationFactory<Program> factory, Guid userId)
    {
        var cache = factory.Services.GetRequiredService<IDistributedCache>();
        return await cache.GetAsync($"cinora:ai:recs:{userId}");
    }

    // Builds a host with BOTH external boundaries faked (TMDB + the engine), leaving the shared factory untouched
    // and restoring the process-global Serilog logger the host build installs (the sibling-suite guard).
    private WebApplicationFactory<Program> CreateFactoryWith(ITmdbClient fakeTmdb, IRecommendationEngine fakeEngine)
    {
        var originalLogger = Log.Logger;
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITmdbClient>();
                services.AddScoped(_ => fakeTmdb);
                services.RemoveAll<IRecommendationEngine>();
                services.AddScoped(_ => fakeEngine);
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

    private static TmdbTitleSummary Summary(int tmdbId, string title) =>
        new()
        {
            TmdbId = tmdbId,
            MediaType = MediaType.Movie,
            Title = title,
            PosterPath = "/candidate.jpg",
            ReleaseDate = new DateOnly(2022, 1, 1),
        };
}
