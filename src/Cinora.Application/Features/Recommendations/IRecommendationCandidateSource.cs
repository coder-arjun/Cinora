using Cinora.Application.Common.Ai;

namespace Cinora.Application.Features.Recommendations;

/// <summary>
/// Generates the candidate universe for a user's recommendations — real, resolvable TMDB titles fanned out from
/// their taste (ADR 0017 §1, Phase 5 design §5.3). An Application-internal seam (interface + implementation both
/// in Application; it depends only on <c>ITmdbClient</c> and <c>IRecommendationPolicy</c>). The returned set is
/// the model's entire allowed output vocabulary and the resolution table the hallucination guard checks against:
/// every candidate is a real TMDB title, deduplicated, with seen titles removed, capped to
/// <c>IRecommendationPolicy.MaxCandidates</c>. It returns Application <see cref="CandidateTitle"/> records only
/// — no TMDB DTO ever leaks (ADR 0006).
/// </summary>
public interface IRecommendationCandidateSource
{
    /// <summary>
    /// Fans out TMDB recommendations for the taste's seed titles and discover-by-genre for its favorite genres,
    /// tops up from popular/top-rated when the fan-out is thin, then merges → dedups by <c>(TmdbId, Media)</c> →
    /// removes the seen-exclusion set → caps to the configured maximum, resolving genre ids to names.
    /// </summary>
    /// <param name="taste">The user's composite taste result (seeds, favorite genres, seen exclusions).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The capped, deduplicated, seen-excluded candidate titles.</returns>
    Task<IReadOnlyList<CandidateTitle>> GenerateAsync(UserTasteResult taste, CancellationToken cancellationToken);
}
