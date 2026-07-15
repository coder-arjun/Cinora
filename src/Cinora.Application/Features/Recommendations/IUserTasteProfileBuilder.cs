namespace Cinora.Application.Features.Recommendations;

/// <summary>
/// Builds one user's <see cref="UserTasteResult"/> from their own reviews, ratings, and watchlist
/// (ADR 0017 §1, Phase 5 design §5.1). An Application-internal seam (interface + implementation both in
/// Application; it depends only on <c>IAppDbContext</c> and <c>IRecommendationPolicy</c>) so the generation
/// handler stays thin and testable. Scoped to a single target <c>userId</c>: only that user's data ever grounds
/// their recommendations (the privacy rule, §12).
/// </summary>
public interface IUserTasteProfileBuilder
{
    /// <summary>
    /// Projects the target user's taste — top-rated titles, watchlist titles, favorite genres, the recommendation
    /// seeds, the favorite-genre TMDB ids, and the seen-exclusion set — with a small number of
    /// <c>AsNoTracking().Select(...)</c> reads (no per-title round-trips).
    /// </summary>
    /// <param name="userId">The user whose taste to build (used only to scope the query — never sent to the model).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The composite taste result; <see cref="UserTasteResult.HasSignal"/> is false for a cold-start user.</returns>
    Task<UserTasteResult> BuildAsync(Guid userId, CancellationToken cancellationToken);
}
