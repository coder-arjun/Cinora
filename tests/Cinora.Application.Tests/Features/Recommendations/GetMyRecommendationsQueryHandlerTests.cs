using Cinora.Application.Common.Interfaces;
using Cinora.Application.Features.Recommendations;
using Cinora.Application.Tests.Common.Fakes;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinora.Application.Tests.Features.Recommendations;

/// <summary>
/// Unit tests for <see cref="GetMyRecommendationsQueryHandler"/> — the LLM-free three-tier serve path (Milestone
/// 5.2, ADR 0018 §4). They prove the order (cache hit → latest history row → heuristic), that the query NEVER
/// writes and NEVER re-warms the cache, that a THROWING cache degrades to the durable fallback (the "never 500"
/// serve contract), that a corrupt history envelope falls through to the heuristic, and — structurally — that the
/// handler has no <see cref="IRecommendationEngine"/> dependency at all (the zero-page-load-LLM exit criterion).
/// </summary>
public sealed class GetMyRecommendationsQueryHandlerTests
{
    [Fact]
    public async Task Handle_returns_the_cached_set_on_a_hit_without_touching_history_or_the_heuristic()
    {
        var userId = Guid.NewGuid();
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        var cache = new FakeServedRecommendationCache();
        await cache.SetAsync(userId, SetOf(RecommendationSource.Ai, 700), CancellationToken.None);
        var heuristic = new FakeHeuristicRecommender();

        var result = await NewHandler(userId, cache, db, heuristic)
            .Handle(new GetMyRecommendationsQuery(), CancellationToken.None);

        Assert.Equal(RecommendationSource.Ai, result.Source);
        Assert.Equal(700, Assert.Single(result.Picks).TmdbId);
        Assert.Equal(0, heuristic.CallCount); // the durable fallback was never consulted
    }

    [Fact]
    public async Task Handle_falls_back_to_the_latest_history_row_on_a_cache_miss()
    {
        var userId = Guid.NewGuid();
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();
        await SeedHistoryAsync(db, userId, SetOf(RecommendationSource.Ai, 815));

        var cache = new FakeServedRecommendationCache(); // empty → miss
        var heuristic = new FakeHeuristicRecommender();

        var result = await NewHandler(userId, cache, db, heuristic)
            .Handle(new GetMyRecommendationsQuery(), CancellationToken.None);

        Assert.Equal(RecommendationSource.Ai, result.Source);
        Assert.Equal(815, Assert.Single(result.Picks).TmdbId);
        Assert.Equal(0, heuristic.CallCount);       // the history row satisfied the request
        Assert.Equal(0, cache.SetCallCount);        // a query NEVER re-warms the cache
    }

    [Fact]
    public async Task Handle_falls_back_to_the_heuristic_when_there_is_no_cache_and_no_history()
    {
        var userId = Guid.NewGuid();
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        var cache = new FakeServedRecommendationCache();
        var heuristic = new FakeHeuristicRecommender { Result = SetOf(RecommendationSource.Heuristic, 500) };

        var result = await NewHandler(userId, cache, db, heuristic)
            .Handle(new GetMyRecommendationsQuery(), CancellationToken.None);

        Assert.Equal(RecommendationSource.Heuristic, result.Source);
        Assert.Equal(500, Assert.Single(result.Picks).TmdbId);
        Assert.Equal(1, heuristic.CallCount);
    }

    [Fact]
    public async Task Handle_survives_a_throwing_cache_and_serves_the_history_row()
    {
        var userId = Guid.NewGuid();
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();
        await SeedHistoryAsync(db, userId, SetOf(RecommendationSource.Ai, 909));

        var cache = new FakeServedRecommendationCache { Throws = true }; // the cache is down
        var heuristic = new FakeHeuristicRecommender();

        var result = await NewHandler(userId, cache, db, heuristic)
            .Handle(new GetMyRecommendationsQuery(), CancellationToken.None);

        Assert.Equal(RecommendationSource.Ai, result.Source);   // never 500 — degraded to the durable row
        Assert.Equal(909, Assert.Single(result.Picks).TmdbId);
        Assert.Equal(0, heuristic.CallCount);
    }

    [Fact]
    public async Task Handle_survives_a_throwing_cache_with_no_history_and_serves_the_heuristic()
    {
        var userId = Guid.NewGuid();
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        var cache = new FakeServedRecommendationCache { Throws = true };
        var heuristic = new FakeHeuristicRecommender { Result = SetOf(RecommendationSource.Heuristic, 42) };

        var result = await NewHandler(userId, cache, db, heuristic)
            .Handle(new GetMyRecommendationsQuery(), CancellationToken.None);

        Assert.Equal(RecommendationSource.Heuristic, result.Source);
        Assert.Equal(42, Assert.Single(result.Picks).TmdbId);
        Assert.Equal(1, heuristic.CallCount);
    }

    [Fact]
    public async Task Handle_falls_through_to_the_heuristic_when_the_history_envelope_is_corrupt()
    {
        var userId = Guid.NewGuid();
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();

        // A history row whose OutputSummary is not a valid envelope (an old-schema / corrupt row).
        db.AIRecommendationHistories.Add(
            AIRecommendationHistory.Create(userId, "old-model", "input", "not a valid envelope", 1, 1));
        await db.SaveChangesAsync(CancellationToken.None);

        var cache = new FakeServedRecommendationCache();
        var heuristic = new FakeHeuristicRecommender { Result = SetOf(RecommendationSource.Heuristic, 7) };

        var result = await NewHandler(userId, cache, db, heuristic)
            .Handle(new GetMyRecommendationsQuery(), CancellationToken.None);

        Assert.Equal(RecommendationSource.Heuristic, result.Source);
        Assert.Equal(1, heuristic.CallCount); // the unparseable row was skipped, not thrown on
    }

    [Fact]
    public void Handler_has_no_recommendation_engine_dependency()
    {
        // Structural proof of the exit criterion: the serve handler cannot invoke the LLM because it does not
        // depend on it. (The integration test additionally asserts a faked engine's CallCount stays 0 on serves.)
        var parameterTypes = typeof(GetMyRecommendationsQueryHandler)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType);

        Assert.DoesNotContain(typeof(IRecommendationEngine), parameterTypes);
    }

    private static GetMyRecommendationsQueryHandler NewHandler(
        Guid userId, IServedRecommendationCache cache, IAppDbContext db, IHeuristicRecommender heuristic) =>
        new(new StubCurrentUser(userId), cache, db, heuristic,
            NullLogger<GetMyRecommendationsQueryHandler>.Instance);

    private static async Task SeedHistoryAsync(TestAppDbContext db, Guid userId, RecommendationSet set)
    {
        var envelope = RecommendationSetEnvelope.Serialize(set, AIRecommendationHistory.SummaryMaxLength);
        db.AIRecommendationHistories.Add(
            AIRecommendationHistory.Create(userId, "seed-model", "seed-input", envelope, 10, 5));
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static RecommendationSet SetOf(RecommendationSource source, int tmdbId) =>
        new()
        {
            Source = source,
            GeneratedAtUtc = new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc),
            Picks =
            [
                new RecommendationPick
                {
                    TmdbId = tmdbId,
                    Media = MediaType.Movie,
                    Title = $"Title {tmdbId}",
                    Reason = "A seeded reason.",
                },
            ],
        };
}
