using Cinora.Application.Common.Ai;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Features.Recommendations;
using Cinora.Application.Tests.Common.Fakes;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinora.Application.Tests.Features.Recommendations;

/// <summary>
/// Unit tests for <see cref="GenerateRecommendationsCommandHandler"/> — the orchestration, the <b>authoritative
/// hallucination guard</b> (Milestone 5.1, ADR 0017 §3), and the <b>persistence + cache-warm</b> writer path
/// (Milestone 5.2, ADR 0018 §3). Driven with hand-rolled fakes for the two seams and the engine plus a real
/// in-process SQLite <see cref="TestAppDbContext"/> and a <see cref="FakeServedRecommendationCache"/> (no LLM, no
/// network). They prove: the guard drops off-list picks, collapses duplicates, caps to <c>MaxResults</c>, and
/// takes card metadata from the candidate; a successful set persists EXACTLY ONE history row (model + token
/// counts from <see cref="AiUsage"/> + a parseable <c>OutputSummary</c>) and warms the cache; and every empty
/// outcome (thin profile, no candidates, engine outage, zero grounded picks) persists NO row and warms NO entry.
/// </summary>
public sealed class GenerateRecommendationsCommandHandlerTests
{
    [Fact]
    public async Task Handle_guard_drops_off_list_picks_dedups_caps_and_takes_card_metadata_from_the_candidate()
    {
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();
        var cache = new FakeServedRecommendationCache();

        var builder = new FakeUserTasteProfileBuilder
        {
            Result = new UserTasteResult { Profile = new TasteProfile(), HasSignal = true },
        };
        var candidateSource = new FakeRecommendationCandidateSource
        {
            Candidates =
            [
                Candidate(700, "Card Seven", posterPath: "/7.jpg", year: 2007),
                Candidate(701, "Card Eight", year: 2008),
                Candidate(702, "Card Nine"),
            ],
        };
        var engine = new FakeRecommendationEngine
        {
            Result = new RecommendationEngineResult
            {
                Usage = new AiUsage { Model = "fake", PromptTokens = 5, CompletionTokens = 3 },
                Picks =
                [
                    Pick(700, "Reason for seven is quite long enough"), // valid → kept #1 (reason trimmed)
                    Pick(999, "hallucinated title not offered"),        // off-list → dropped
                    Pick(701, "Reason eight"),                          // valid → kept #2
                    Pick(701, "duplicate of eight"),                    // duplicate → collapsed
                    Pick(702, "Reason nine"),                           // valid but MaxResults reached → not kept
                ],
            },
        };
        var policy = new StubRecommendationPolicy { MaxResults = 2, MaxReasonLength = 10 };
        var handler = NewHandler(builder, candidateSource, engine, policy, db, cache);

        var set = await handler.Handle(new GenerateRecommendationsCommand(Guid.NewGuid()), CancellationToken.None);

        // Capped to MaxResults, only grounded ids, in engine order, the off-list 999 and the capped 702 absent.
        Assert.Equal(RecommendationSource.Ai, set.Source);
        Assert.Equal(2, set.Picks.Count);
        Assert.Equal(700, set.Picks[0].TmdbId);
        Assert.Equal(701, set.Picks[1].TmdbId);
        Assert.DoesNotContain(set.Picks, pick => pick.TmdbId == 999); // hallucination dropped
        Assert.DoesNotContain(set.Picks, pick => pick.TmdbId == 702); // over-cap

        // Card metadata comes from the CANDIDATE (grounded), not the model.
        Assert.Equal("Card Seven", set.Picks[0].Title);
        Assert.Equal("/7.jpg", set.Picks[0].PosterPath);
        Assert.Equal(2007, set.Picks[0].ReleaseYear);

        // Only the reason comes from the model, trimmed to the policy cap.
        Assert.True(set.Picks[0].Reason.Length <= policy.MaxReasonLength);
        Assert.Equal("Reason for", set.Picks[0].Reason);
    }

    [Fact]
    public async Task Handle_successful_set_persists_exactly_one_history_row_with_token_usage_and_warms_the_cache()
    {
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();
        var cache = new FakeServedRecommendationCache();

        var userId = Guid.NewGuid();
        var builder = new FakeUserTasteProfileBuilder
        {
            Result = new UserTasteResult
            {
                // A non-trivial profile so InputSummary is meaningful (and PII-free — titles/genres/ratings only).
                Profile = new TasteProfile
                {
                    TopRated = [new RatedTitle { Title = "Interstellar", Year = 2014, Rating = 9, Genres = ["Sci-Fi"] }],
                    FavoriteGenres = ["Sci-Fi", "Drama"],
                },
                HasSignal = true,
            },
        };
        var candidateSource = new FakeRecommendationCandidateSource
        {
            Candidates = [Candidate(700, "Card Seven", posterPath: "/7.jpg", year: 2007), Candidate(701, "Card Eight")],
        };
        var engine = new FakeRecommendationEngine
        {
            Result = new RecommendationEngineResult
            {
                Usage = new AiUsage { Model = "llama-under-test", PromptTokens = 137, CompletionTokens = 42 },
                Picks = [Pick(700, "Because you like space epics."), Pick(701, "A tense thriller you'll enjoy.")],
            },
        };
        var handler = NewHandler(builder, candidateSource, engine, new StubRecommendationPolicy(), db, cache);

        var set = await handler.Handle(new GenerateRecommendationsCommand(userId), CancellationToken.None);

        // Exactly ONE row, for this user, carrying the model + the token counts straight from AiUsage.
        var row = Assert.Single(await db.AIRecommendationHistories.AsNoTracking().ToListAsync());
        Assert.Equal(userId, row.UserId);
        Assert.Equal("llama-under-test", row.Model);
        Assert.Equal(137, row.PromptTokens);
        Assert.Equal(42, row.CompletionTokens);

        // InputSummary is a bounded, PII-free audit of the grounding (titles/genres, never the user id).
        Assert.DoesNotContain(userId.ToString(), row.InputSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Interstellar", row.InputSummary, StringComparison.Ordinal);

        // OutputSummary is a self-contained, parseable envelope that round-trips the served set.
        Assert.True(RecommendationSetEnvelope.TryParse(row.OutputSummary, out var parsed));
        Assert.Equal(RecommendationSource.Ai, parsed.Source);
        Assert.Equal([700, 701], parsed.Picks.Select(pick => pick.TmdbId));
        Assert.Equal("Card Seven", parsed.Picks[0].Title);

        // The cache was warmed exactly once with the same set the handler returned.
        Assert.Equal(1, cache.SetCallCount);
        var cached = await cache.GetAsync(userId, CancellationToken.None);
        Assert.NotNull(cached);
        Assert.Equal(set.Picks.Select(pick => pick.TmdbId), cached.Picks.Select(pick => pick.TmdbId));
    }

    [Fact]
    public async Task Handle_thin_profile_returns_an_empty_set_without_calling_the_engine_or_persisting()
    {
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();
        var cache = new FakeServedRecommendationCache();

        var builder = new FakeUserTasteProfileBuilder
        {
            Result = new UserTasteResult { Profile = new TasteProfile(), HasSignal = false },
        };
        var candidateSource = new FakeRecommendationCandidateSource();
        var engine = new FakeRecommendationEngine();
        var handler = NewHandler(builder, candidateSource, engine, new StubRecommendationPolicy(), db, cache);

        var set = await handler.Handle(new GenerateRecommendationsCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.Empty(set.Picks);
        Assert.Equal(RecommendationSource.Ai, set.Source);
        Assert.Equal(0, engine.CallCount);          // the model was never invoked (§5.1 cold-start)
        Assert.Equal(0, candidateSource.CallCount); // no candidates were even generated
        await AssertNothingPersistedOrWarmed(db, cache);
    }

    [Fact]
    public async Task Handle_no_candidates_returns_an_empty_set_without_calling_the_engine_or_persisting()
    {
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();
        var cache = new FakeServedRecommendationCache();

        var builder = new FakeUserTasteProfileBuilder
        {
            Result = new UserTasteResult { Profile = new TasteProfile(), HasSignal = true },
        };
        var candidateSource = new FakeRecommendationCandidateSource { Candidates = [] };
        var engine = new FakeRecommendationEngine();
        var handler = NewHandler(builder, candidateSource, engine, new StubRecommendationPolicy(), db, cache);

        var set = await handler.Handle(new GenerateRecommendationsCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.Empty(set.Picks);
        Assert.Equal(1, candidateSource.CallCount);
        Assert.Equal(0, engine.CallCount);
        await AssertNothingPersistedOrWarmed(db, cache);
    }

    [Fact]
    public async Task Handle_engine_outage_degrades_to_an_empty_set_without_throwing_or_persisting()
    {
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();
        var cache = new FakeServedRecommendationCache();

        var builder = new FakeUserTasteProfileBuilder
        {
            Result = new UserTasteResult { Profile = new TasteProfile(), HasSignal = true },
        };
        var candidateSource = new FakeRecommendationCandidateSource { Candidates = [Candidate(700, "Card Seven")] };
        var engine = new FakeRecommendationEngine { Throws = true };
        var handler = NewHandler(builder, candidateSource, engine, new StubRecommendationPolicy(), db, cache);

        var set = await handler.Handle(new GenerateRecommendationsCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.Empty(set.Picks);
        Assert.Equal(RecommendationSource.Ai, set.Source);
        Assert.Equal(1, engine.CallCount); // the engine WAS called, and its failure was swallowed
        await AssertNothingPersistedOrWarmed(db, cache);
    }

    [Fact]
    public async Task Handle_zero_grounded_picks_persists_nothing_and_warms_nothing()
    {
        using var store = new SharedSqliteStore();
        await using var db = store.CreateContext();
        var cache = new FakeServedRecommendationCache();

        var builder = new FakeUserTasteProfileBuilder
        {
            Result = new UserTasteResult { Profile = new TasteProfile(), HasSignal = true },
        };
        var candidateSource = new FakeRecommendationCandidateSource { Candidates = [Candidate(700, "Card Seven")] };
        var engine = new FakeRecommendationEngine
        {
            // The model returns only an off-list (hallucinated) pick → the guard keeps zero → a generation miss.
            Result = new RecommendationEngineResult
            {
                Usage = new AiUsage { Model = "fake", PromptTokens = 8, CompletionTokens = 4 },
                Picks = [Pick(999, "A title that was never offered.")],
            },
        };
        var handler = NewHandler(builder, candidateSource, engine, new StubRecommendationPolicy(), db, cache);

        var set = await handler.Handle(new GenerateRecommendationsCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.Empty(set.Picks);
        Assert.Equal(1, engine.CallCount);
        await AssertNothingPersistedOrWarmed(db, cache);
    }

    private static async Task AssertNothingPersistedOrWarmed(TestAppDbContext db, FakeServedRecommendationCache cache)
    {
        Assert.Empty(await db.AIRecommendationHistories.AsNoTracking().ToListAsync());
        Assert.Equal(0, cache.SetCallCount);
    }

    private static GenerateRecommendationsCommandHandler NewHandler(
        FakeUserTasteProfileBuilder builder,
        FakeRecommendationCandidateSource candidateSource,
        FakeRecommendationEngine engine,
        StubRecommendationPolicy policy,
        IAppDbContext db,
        IServedRecommendationCache cache) =>
        new(builder, candidateSource, engine, policy, db, cache, TimeProvider.System,
            NullLogger<GenerateRecommendationsCommandHandler>.Instance);

    private static CandidateTitle Candidate(int tmdbId, string title, string? posterPath = null, int? year = null) =>
        new()
        {
            TmdbId = tmdbId,
            Media = MediaType.Movie,
            Title = title,
            PosterPath = posterPath,
            Year = year,
        };

    private static RankedPick Pick(int tmdbId, string reason) =>
        new() { TmdbId = tmdbId, Media = MediaType.Movie, Reason = reason };
}
