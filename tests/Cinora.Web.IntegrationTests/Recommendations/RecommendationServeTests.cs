using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Tmdb;
using Cinora.Application.Features.Recommendations;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Domain.ValueObjects;
using Cinora.Infrastructure.Identity;
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
/// Milestone 5.2 end-to-end test for history persistence + the served cache + the three-tier, LLM-free serve
/// path (ADR 0018), over the migrated <c>CinoraTest</c> LocalDB. It drives the REAL Application <see cref="ISender"/>,
/// the REAL <see cref="ServedRecommendationCache"/> adapter, and the REAL heuristic — only the two external
/// boundaries are faked (a <see cref="FakeTmdbClient"/> and a <see cref="FakeRecommendationEngine"/>), plus a
/// <see cref="MutableCurrentUser"/> so the owner-scoped query can be dispatched from a background scope. It proves:
/// a generation persists exactly one history row (non-zero tokens + a parseable envelope) and warms the cache;
/// <c>GetMyRecommendationsQuery</c> serves the cache hit, then (after eviction) the history row, then — for a
/// fresh user with no history — the deterministic heuristic; and the engine is invoked exactly once (the single
/// generation) and NEVER on a serve. TMDB ids sit in the 973_xxx range to stay isolated from the sibling suites.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class RecommendationServeTests : IAsyncLifetime
{
    private const int SeenReviewedTmdbId = 973_001;
    private const int RecommendedATmdbId = 973_010;
    private const int RecommendedBTmdbId = 973_011;
    private const int PopularTmdbId = 973_050;
    private const int FavoriteGenreTmdbId = 973_028;

    private readonly CinoraWebApplicationFactory _factory;

    public RecommendationServeTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Generate_persists_history_and_serve_returns_cache_then_history_then_heuristic_without_the_llm()
    {
        var primaryUser = await RegisterServerUserAsync("Serena Serve 5.2");
        await SeedTasteAsync(primaryUser);

        // A second user with NO taste and NO history — their serve must fall all the way to the heuristic.
        var freshUser = await RegisterServerUserAsync("Nate Newuser 5.2");

        var fakeTmdb = new FakeTmdbClient
        {
            // Drives the heuristic's global-popular tier for the fresh user (no signal → popular movies).
            PopularMovies = [Summary(PopularTmdbId, "Popular Pick")],
        };
        fakeTmdb.Recommendations[(MediaType.Movie, SeenReviewedTmdbId)] =
        [
            Summary(RecommendedATmdbId, "Recommended A"),
            Summary(RecommendedBTmdbId, "Recommended B"),
        ];

        var fakeEngine = new FakeRecommendationEngine();
        var currentUser = new MutableCurrentUser();

        using var factory = CreateFactoryWith(fakeTmdb, fakeEngine, currentUser);

        // 1) GENERATE (the only writer) → exactly one history row with non-zero tokens + a parseable envelope.
        var generated = await SendAsync(factory, new GenerateRecommendationsCommand(primaryUser));
        Assert.Equal(RecommendationSource.Ai, generated.Source);
        Assert.NotEmpty(generated.Picks);

        var row = await SingleHistoryRowAsync(factory, primaryUser);
        Assert.Equal("fake-integration", row.Model);
        Assert.True(row.PromptTokens > 0, "prompt tokens should be recorded from the engine's usage");
        Assert.True(row.CompletionTokens > 0, "completion tokens should be recorded from the engine's usage");
        Assert.True(RecommendationSetEnvelope.TryParse(row.OutputSummary, out var parsedRow));
        Assert.NotEmpty(parsedRow.Picks);

        // 2) SERVE → cache hit (the generation warmed it). Same picks, Source = Ai, no engine call.
        currentUser.UserId = primaryUser;
        var fromCache = await SendAsync(factory, new GetMyRecommendationsQuery());
        Assert.Equal(RecommendationSource.Ai, fromCache.Source);
        Assert.Equal(
            generated.Picks.Select(pick => pick.TmdbId).OrderBy(id => id),
            fromCache.Picks.Select(pick => pick.TmdbId).OrderBy(id => id));

        // 3) Evict the served-cache key → SERVE falls back to the latest history row (still Ai, still no LLM).
        await EvictServedCacheAsync(factory, primaryUser);
        var fromHistory = await SendAsync(factory, new GetMyRecommendationsQuery());
        Assert.Equal(RecommendationSource.Ai, fromHistory.Source);
        Assert.Equal(
            generated.Picks.Select(pick => pick.TmdbId).OrderBy(id => id),
            fromHistory.Picks.Select(pick => pick.TmdbId).OrderBy(id => id));

        // 4) A FRESH user with no cache and no history → the deterministic heuristic (faked popular), never the LLM.
        currentUser.UserId = freshUser;
        var heuristicSet = await SendAsync(factory, new GetMyRecommendationsQuery());
        Assert.Equal(RecommendationSource.Heuristic, heuristicSet.Source);
        Assert.Contains(heuristicSet.Picks, pick => pick.TmdbId == PopularTmdbId);

        // The engine ran EXACTLY ONCE — the single generation. Every serve (cache, history, heuristic) was LLM-free.
        Assert.Equal(1, fakeEngine.CallCount);
    }

    // Seeds the primary user's taste: one reviewed movie (linked to a favorite genre → seeds the recommendations
    // fan-out) so the grounded generation has candidates to rank.
    private async Task SeedTasteAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

        var genre = Genre.Create(FavoriteGenreTmdbId, "Serve Action 973");
        db.Genres.Add(genre);

        var reviewed = Movie.FromTmdb(
            SeenReviewedTmdbId, MediaType.Movie, "Seen Reviewed 973", null, new DateTime(2014, 11, 7), "/r.jpg", null);
        db.Movies.Add(reviewed);
        db.MovieGenres.Add(MovieGenre.Link(reviewed.Id, genre.Id));
        db.Reviews.Add(Review.Create(userId, reviewed.Id, Rating.From(9), "A grounded seed review."));

        await db.SaveChangesAsync();
    }

    // Creates a user (ApplicationUser + domain User) through the atomic registration seam; the history row's
    // Restrict FK to User (and the review's) needs a real row.
    private async Task<Guid> RegisterServerUserAsync(string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var registration = scope.ServiceProvider.GetRequiredService<IUserRegistrationService>();
        var result = await registration.RegisterAsync(
            $"serve-{Guid.NewGuid():N}@cinora.test", "Test1234!", $"{displayName} {Guid.NewGuid():N}", CancellationToken.None);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Seed registration for '{displayName}' failed.");
        }

        return result.UserId;
    }

    private static async Task<AIRecommendationHistory> SingleHistoryRowAsync(
        WebApplicationFactory<Program> factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Set<AIRecommendationHistory>().AsNoTracking()
            .Where(history => history.UserId == userId)
            .SingleAsync();
    }

    // Evicts the adapter's per-user key (which must match ServedRecommendationCache.Key) via the same singleton
    // IDistributedCache the adapter uses, so the next serve is a genuine cache miss.
    private static async Task EvictServedCacheAsync(WebApplicationFactory<Program> factory, Guid userId)
    {
        var cache = factory.Services.GetRequiredService<IDistributedCache>();
        await cache.RemoveAsync($"cinora:ai:recs:{userId}");
    }

    // Builds a host with BOTH external boundaries faked (TMDB + the engine) and the current-user seam made
    // settable, leaving the shared factory untouched and restoring the process-global Serilog logger the host
    // build installs (the sibling-suite guard).
    private WebApplicationFactory<Program> CreateFactoryWith(
        ITmdbClient fakeTmdb, IRecommendationEngine fakeEngine, ICurrentUser currentUser)
    {
        var originalLogger = Log.Logger;
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITmdbClient>();
                services.AddScoped(_ => fakeTmdb);
                services.RemoveAll<IRecommendationEngine>();
                services.AddScoped(_ => fakeEngine);
                services.RemoveAll<ICurrentUser>();
                services.AddSingleton(currentUser);
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

    private static async Task<TResponse> SendAsync<TResponse>(
        WebApplicationFactory<Program> factory, IRequest<TResponse> request)
    {
        using var scope = factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        return await sender.Send(request);
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
