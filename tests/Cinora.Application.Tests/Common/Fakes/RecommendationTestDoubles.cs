using Cinora.Application.Common.Ai;
using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Tmdb;
using Cinora.Application.Features.Discovery;
using Cinora.Application.Features.Recommendations;
using Cinora.Domain.Enums;

namespace Cinora.Application.Tests.Common.Fakes;

/// <summary>
/// A settable <see cref="IRecommendationPolicy"/> for the Milestone 5.1 recommendation tests: each cap is a
/// mutable property defaulting to the production default, so a test can shrink (e.g.) <see cref="MaxCandidates"/>
/// or <see cref="MaxResults"/> to prove the cap without seeding dozens of titles. Hand-rolled — no mocking
/// package, consistent with the rest of the suite.
/// </summary>
internal sealed class StubRecommendationPolicy : IRecommendationPolicy
{
    public int MaxTopRated { get; set; } = 10;

    public int MaxWatchlistTitles { get; set; } = 10;

    public int MaxFavoriteGenres { get; set; } = 5;

    public int MaxRecommendationSeeds { get; set; } = 5;

    public int MaxCandidates { get; set; } = 40;

    public int MaxResults { get; set; } = 12;

    public int MaxReasonLength { get; set; } = 140;
}

/// <summary>
/// A hand-rolled <see cref="IRecommendationEngine"/> for the generation-handler tests. It records how many
/// times it was called (so a test can assert the engine was NOT called on a thin profile / empty candidate set),
/// the request it last received (so a test can inspect the grounding), returns a settable canned
/// <see cref="Result"/>, and can be flipped to <see cref="Throws"/> a <see cref="RecommendationEngineException"/>
/// to prove the handler degrades. No LLM, no network.
/// </summary>
internal sealed class FakeRecommendationEngine : IRecommendationEngine
{
    /// <summary>The number of times <see cref="RankAsync"/> was invoked.</summary>
    public int CallCount { get; private set; }

    /// <summary>When <see langword="true"/>, <see cref="RankAsync"/> throws to model a model outage.</summary>
    public bool Throws { get; set; }

    /// <summary>The request the engine last received (for grounding assertions).</summary>
    public RecommendationRequest? LastRequest { get; private set; }

    /// <summary>The canned result the engine returns when it does not throw.</summary>
    public RecommendationEngineResult Result { get; set; } = new()
    {
        Picks = [],
        Usage = new AiUsage { Model = "fake", PromptTokens = 0, CompletionTokens = 0 },
    };

    public Task<RecommendationEngineResult> RankAsync(RecommendationRequest request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;
        return Throws
            ? throw new RecommendationEngineException("Simulated recommendation-model outage.")
            : Task.FromResult(Result);
    }
}

/// <summary>
/// A hand-rolled <see cref="IUserTasteProfileBuilder"/> that returns a settable <see cref="Result"/> and records
/// its call count — so the generation-handler tests can drive the profile directly (thin vs. rich) without a
/// database.
/// </summary>
internal sealed class FakeUserTasteProfileBuilder : IUserTasteProfileBuilder
{
    public int CallCount { get; private set; }

    public UserTasteResult Result { get; set; } = new() { Profile = new TasteProfile(), HasSignal = true };

    public Task<UserTasteResult> BuildAsync(Guid userId, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(Result);
    }
}

/// <summary>
/// A hand-rolled <see cref="IRecommendationCandidateSource"/> that returns a settable candidate list and records
/// its call count — so the generation-handler tests can offer an exact candidate set to the guard without TMDB.
/// </summary>
internal sealed class FakeRecommendationCandidateSource : IRecommendationCandidateSource
{
    public int CallCount { get; private set; }

    public IReadOnlyList<CandidateTitle> Candidates { get; set; } = [];

    public Task<IReadOnlyList<CandidateTitle>> GenerateAsync(UserTasteResult taste, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(Candidates);
    }
}

/// <summary>
/// A hand-rolled <see cref="IServedRecommendationCache"/> for the Milestone 5.2 tests: an in-memory dictionary of
/// per-user sets plus a settable <see cref="Throws"/> mode. Because the real adapter degrades internally and
/// never throws, the fake's throwing mode is how a test proves the SERVE PATH itself stays a "never 500" (the
/// query falls through to the history row / heuristic). Records get/set call counts so a test can assert a
/// generation warmed the cache exactly once and an empty generation warmed nothing.
/// </summary>
internal sealed class FakeServedRecommendationCache : IServedRecommendationCache
{
    private readonly Dictionary<Guid, RecommendationSet> _store = [];

    /// <summary>When <see langword="true"/>, both operations throw — modelling a cache the serve path must survive.</summary>
    public bool Throws { get; set; }

    /// <summary>The number of times <see cref="GetAsync"/> was invoked.</summary>
    public int GetCallCount { get; private set; }

    /// <summary>The number of times <see cref="SetAsync"/> was invoked.</summary>
    public int SetCallCount { get; private set; }

    public Task<RecommendationSet?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        GetCallCount++;
        return Throws
            ? throw new InvalidOperationException("Simulated served-recommendation cache outage.")
            : Task.FromResult<RecommendationSet?>(_store.GetValueOrDefault(userId));
    }

    public Task SetAsync(Guid userId, RecommendationSet recommendationSet, CancellationToken cancellationToken)
    {
        SetCallCount++;
        if (Throws)
        {
            throw new InvalidOperationException("Simulated served-recommendation cache outage.");
        }

        _store[userId] = recommendationSet;
        return Task.CompletedTask;
    }
}

/// <summary>
/// A hand-rolled <see cref="IHeuristicRecommender"/> that returns a settable <see cref="Result"/> and records its
/// call count — so the serve-query tests can assert the deterministic fallback tier is (or is not) reached without
/// touching TMDB. The real heuristic is exercised separately against SQLite in <c>HeuristicRecommenderTests</c>.
/// </summary>
internal sealed class FakeHeuristicRecommender : IHeuristicRecommender
{
    public int CallCount { get; private set; }

    public RecommendationSet Result { get; set; } = new()
    {
        Picks = [],
        Source = RecommendationSource.Heuristic,
        GeneratedAtUtc = DateTime.UtcNow,
    };

    public Task<RecommendationSet> RecommendAsync(Guid userId, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(Result);
    }
}

/// <summary>
/// A fully configurable <see cref="ITmdbClient"/> for the candidate-source tests: per-title recommendations,
/// per-media discover / popular / top-rated fan-outs, and per-media genre lists — all canned via public
/// dictionaries. The reads the candidate source does not make (trending / search / details) throw, so an
/// unexpected call is a loud test-wiring bug. No network, no TMDB key.
/// </summary>
internal sealed class RecommendationTmdbClientStub : ITmdbClient
{
    public Dictionary<(MediaType Media, int TmdbId), IReadOnlyList<TmdbTitleSummary>> RecommendationsByTitle { get; } = [];

    public Dictionary<MediaType, IReadOnlyList<TmdbTitleSummary>> DiscoverByMediaType { get; } = [];

    public Dictionary<MediaType, IReadOnlyList<TmdbTitleSummary>> PopularByMediaType { get; } = [];

    public Dictionary<MediaType, IReadOnlyList<TmdbTitleSummary>> TopRatedByMediaType { get; } = [];

    public Dictionary<MediaType, IReadOnlyList<TmdbGenre>> GenresByMediaType { get; } = [];

    /// <summary>When set, <see cref="GetRecommendationsAsync"/> throws — modelling a TMDB upstream outage the
    /// heuristic must survive (CachedTmdbClient re-throws the inner TmdbClient failure). Per-tier so a test can
    /// prove partial results survive a mid-fan-out failure.</summary>
    public bool ThrowOnRecommendations { get; set; }

    /// <summary>When set, <see cref="DiscoverByGenreAsync"/> throws (see <see cref="ThrowOnRecommendations"/>).</summary>
    public bool ThrowOnDiscover { get; set; }

    /// <summary>When set, <see cref="GetPopularAsync"/> throws (see <see cref="ThrowOnRecommendations"/>).</summary>
    public bool ThrowOnPopular { get; set; }

    /// <summary>When set, <see cref="GetTopRatedAsync"/> throws (see <see cref="ThrowOnRecommendations"/>).</summary>
    public bool ThrowOnTopRated { get; set; }

    public Task<IReadOnlyList<TmdbTitleSummary>> GetRecommendationsAsync(MediaType media, int tmdbId, CancellationToken cancellationToken) =>
        ThrowOnRecommendations
            ? throw new InvalidOperationException("Simulated TMDB recommendations outage.")
            : Task.FromResult(RecommendationsByTitle.GetValueOrDefault((media, tmdbId), []));

    public Task<IReadOnlyList<TmdbTitleSummary>> DiscoverByGenreAsync(MediaType media, IReadOnlyList<int> genreTmdbIds, CancellationToken cancellationToken) =>
        ThrowOnDiscover
            ? throw new InvalidOperationException("Simulated TMDB discover outage.")
            : Task.FromResult<IReadOnlyList<TmdbTitleSummary>>(
                genreTmdbIds.Count == 0 ? [] : DiscoverByMediaType.GetValueOrDefault(media, []));

    public Task<IReadOnlyList<TmdbTitleSummary>> GetPopularAsync(MediaType media, CancellationToken cancellationToken) =>
        ThrowOnPopular
            ? throw new InvalidOperationException("Simulated TMDB popular outage.")
            : Task.FromResult(PopularByMediaType.GetValueOrDefault(media, []));

    public Task<IReadOnlyList<TmdbTitleSummary>> GetTopRatedAsync(MediaType media, CancellationToken cancellationToken) =>
        ThrowOnTopRated
            ? throw new InvalidOperationException("Simulated TMDB top-rated outage.")
            : Task.FromResult(TopRatedByMediaType.GetValueOrDefault(media, []));

    public Task<IReadOnlyList<TmdbGenre>> GetGenresAsync(MediaType media, CancellationToken cancellationToken) =>
        Task.FromResult(GenresByMediaType.GetValueOrDefault(media, []));

    public Task<IReadOnlyList<TmdbTitleSummary>> GetTrendingAsync(MediaType media, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<TmdbPage<TmdbTitleSummary>> SearchAsync(MediaType media, string query, int page, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<TmdbTitleDetails?> GetDetailsAsync(MediaType media, int tmdbId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<TmdbPage<TmdbTitleSummary>> SearchMultiAsync(string query, int page, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<TmdbTitleSummary>> GetRegionalRailAsync(MediaType media, RailKind kind, string originalLanguage, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<TmdbPersonCredits?> GetPersonCreditsAsync(int personId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
