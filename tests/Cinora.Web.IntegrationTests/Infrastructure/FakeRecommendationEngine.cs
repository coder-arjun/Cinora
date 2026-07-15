using Cinora.Application.Common.Ai;
using Cinora.Application.Common.Interfaces;

namespace Cinora.Web.IntegrationTests.Infrastructure;

/// <summary>
/// A configurable in-memory <see cref="IRecommendationEngine"/> for the Milestone 5.1 integration test. It
/// replaces the real Ollama-backed engine via <c>ConfigureTestServices</c> so no LLM is called. By default it
/// "ranks" by echoing the offered candidates straight back (every echoed pick is therefore grounded), and any
/// <see cref="ExtraPicks"/> configured are appended UN-grounded — modelling a hallucination the handler's guard
/// must drop. Token usage is canned and non-zero.
/// </summary>
internal sealed class FakeRecommendationEngine : IRecommendationEngine
{
    /// <summary>Off-list picks appended to the echoed candidates to model hallucinations the guard must drop.</summary>
    public IReadOnlyList<RankedPick> ExtraPicks { get; init; } = [];

    /// <summary>The number of times <see cref="RankAsync"/> was invoked.</summary>
    public int CallCount { get; private set; }

    public Task<RecommendationEngineResult> RankAsync(RecommendationRequest request, CancellationToken cancellationToken)
    {
        CallCount++;

        var picks = request.Candidates
            .Select(candidate => new RankedPick
            {
                TmdbId = candidate.TmdbId,
                Media = candidate.Media,
                Reason = $"Because it matches your taste ({candidate.Title}).",
            })
            .Concat(ExtraPicks)
            .ToList();

        return Task.FromResult(new RecommendationEngineResult
        {
            Picks = picks,
            Usage = new AiUsage { Model = "fake-integration", PromptTokens = 10, CompletionTokens = 5 },
        });
    }
}
