namespace Cinora.Application.Features.Recommendations;

/// <summary>
/// The deterministic, non-AI recommender that ALWAYS produces a set using only the cached <c>ITmdbClient</c> and
/// the user's own taste — no model, no vendor call (ADR 0018 §4 tier 3, Phase 5 design §9). It is the serve
/// path's last resort (<c>GetMyRecommendationsQuery</c> tier c): when neither the served cache nor a history row
/// exists (a brand-new user, or the precompute never ran), it renders "more like your highest-rated" / "popular
/// in your favorite genres" / global-popular picks with honest template reasons, so the For-You surfaces never
/// 500 and never block on the LLM. An Application-internal seam (interface + implementation both in Application).
/// </summary>
public interface IHeuristicRecommender
{
    /// <summary>
    /// Builds a deterministic recommendation set for the user from cached TMDB fan-outs, excluding titles the
    /// user has already seen, deduplicated and capped to the policy's result count, tagged
    /// <see cref="RecommendationSource.Heuristic"/>. Makes zero LLM calls.
    /// </summary>
    /// <param name="userId">The user to recommend for (scopes the taste query — never sent to any model).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A non-empty heuristic set when TMDB is reachable; an empty heuristic set otherwise.</returns>
    Task<RecommendationSet> RecommendAsync(Guid userId, CancellationToken cancellationToken);
}
