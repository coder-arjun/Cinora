using Cinora.Application.Common.Ai;

namespace Cinora.Application.Common.Interfaces;

/// <summary>
/// The port for ranking and explaining movie/series recommendations with a Large Language Model. This narrow
/// interface is the entire contract Application knows about the LLM: it takes a user's taste profile plus a
/// candidate set of real titles and returns a ranked, explained subset with token usage. The engine ONLY ranks
/// and explains the supplied candidates — it never reads the database, calls TMDB, resolves the catalog,
/// persists, or invents a title (those are Application concerns; ADR 0016/0017). Defined in Application so
/// handlers depend only on this abstraction and are testable with a fake: the vendor client
/// (Microsoft.Extensions.AI's <c>IChatClient</c> and the OpenAI-compatible types) lives entirely inside the
/// Infrastructure adapter and never crosses this boundary. The free default implementation targets a local
/// Ollama model; a free-tier cloud model is a configuration swap, not a code change.
/// </summary>
public interface IRecommendationEngine
{
    /// <summary>
    /// Ranks and explains the request's candidate titles against the user's taste profile, returning the best
    /// matches (drawn only from the candidates) with a short reason each and the model's reported token usage.
    /// </summary>
    /// <remarks>
    /// The implementation returns only picks drawn from <see cref="RecommendationRequest.Candidates"/>; the
    /// caller enforces this as a hallucination guard regardless. A model that returns nothing usable (an empty
    /// or malformed body) yields an empty <see cref="RecommendationEngineResult.Picks"/> — NOT an exception —
    /// so the caller can degrade to its fallback. A genuine transport failure or the per-request timeout throws
    /// <see cref="Cinora.Application.Common.Exceptions.RecommendationEngineException"/>. Both the configured
    /// per-request timeout and the supplied <paramref name="cancellationToken"/> are honored.
    /// </remarks>
    /// <param name="request">The taste profile and the candidate set to rank.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The ranked, explained picks and the model's token usage.</returns>
    Task<RecommendationEngineResult> RankAsync(RecommendationRequest request, CancellationToken cancellationToken);
}
