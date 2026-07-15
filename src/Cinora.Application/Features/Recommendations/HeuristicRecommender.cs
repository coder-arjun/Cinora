using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Tmdb;
using Cinora.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Cinora.Application.Features.Recommendations;

/// <summary>
/// The Application implementation of <see cref="IHeuristicRecommender"/> over <see cref="ITmdbClient"/> (cached)
/// and the shared <see cref="IUserTasteProfileBuilder"/> (Phase 5 design §9). It reuses the same taste projection
/// + seen-exclusion set as the AI pipeline, then fans out with <b>honest, per-tier template reasons</b>:
/// <list type="bullet">
///   <item>"more like your highest-rated" — <c>GetRecommendationsAsync</c> per seed, reason
///   <c>"Because you rated {Title} {Rating}/10."</c> (the seed's own title/rating, which is why the seed keys and
///   the profile's top-rated titles are consumed in parallel);</item>
///   <item>"popular in your favorite genres" — <c>DiscoverByGenreAsync</c>, reason <c>"Popular in {Genre}."</c>;</item>
///   <item>global-popular top-up (and the WHOLE story for a brand-new, no-signal user) — <c>GetPopularAsync</c>
///   then <c>GetTopRatedAsync</c>, reason <c>"Popular on Cinora right now."</c>.</item>
/// </list>
/// Every accumulated title is excluded if seen, deduplicated by <c>(TmdbId, Media)</c>, and the pool is capped to
/// <see cref="IRecommendationPolicy.MaxResults"/>. Card metadata comes from the TMDB summary; there is no model
/// and no <see cref="IRecommendationEngine"/> dependency — the zero-LLM guarantee holds by construction.
/// </summary>
public sealed class HeuristicRecommender(
    IUserTasteProfileBuilder tasteProfileBuilder,
    ITmdbClient tmdb,
    IRecommendationPolicy policy,
    TimeProvider timeProvider,
    ILogger<HeuristicRecommender> logger)
    : IHeuristicRecommender
{
    private const string PopularReason = "Popular on Cinora right now.";

    /// <inheritdoc />
    public async Task<RecommendationSet> RecommendAsync(Guid userId, CancellationToken cancellationToken)
    {
        var taste = await tasteProfileBuilder.BuildAsync(userId, cancellationToken);

        var picks = new List<RecommendationPick>();
        var keys = new HashSet<(int TmdbId, MediaType Media)>();

        // Accumulate the fan-out, excluding seen titles + duplicates, capping to MaxResults, tagging the reason.
        void Accumulate(IReadOnlyList<TmdbTitleSummary> items, string reason)
        {
            var cappedReason = RecommendationText.Truncate(reason, policy.MaxReasonLength);
            foreach (var item in items)
            {
                if (picks.Count >= policy.MaxResults)
                {
                    break;
                }

                var key = (item.TmdbId, item.MediaType);
                if (taste.SeenExclusions.Contains(key) || !keys.Add(key))
                {
                    continue; // already seen by the user, or already picked
                }

                picks.Add(new RecommendationPick
                {
                    TmdbId = item.TmdbId,
                    Media = item.MediaType,
                    Title = item.Title,
                    PosterPath = item.PosterPath,
                    ReleaseYear = item.ReleaseDate?.Year,
                    Reason = cappedReason,
                });
            }
        }

        bool IsFull() => picks.Count >= policy.MaxResults;

        // The heuristic MUST honor its "empty (or partial) set otherwise" contract (IHeuristicRecommender / ADR
        // 0018 §6 / design §9 — the serve path never 500s). CachedTmdbClient degrades only on CACHE faults; it
        // re-throws the inner TmdbClient failure (HTTP non-success, exhausted resilience, open circuit). So a TMDB
        // throw is caught here (metrics-only Warning — no response bodies), the failing tier yields no titles, and
        // the fan-out continues with what earlier tiers already gathered. Cancellation is never swallowed.
        async Task<IReadOnlyList<TmdbTitleSummary>> SafeFetchAsync(Func<Task<IReadOnlyList<TmdbTitleSummary>>> fetch)
        {
            try
            {
                return await fetch();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                RecommendationsLog.HeuristicTmdbFailed(logger, exception, userId);
                return [];
            }
        }

        // The media types the user engages with (from their seen titles); default to Movie for a cold-start user.
        var presentMedia = taste.SeenExclusions.Select(key => key.Media).Distinct().ToList();
        if (presentMedia.Count == 0)
        {
            presentMedia = [MediaType.Movie];
        }

        // 1) "More like your highest-rated." SeedKeys[i] and Profile.TopRated[i] are parallel by construction
        //    (both projected from the same rated rows), so the seed's own title/rating drives an honest reason.
        var topRated = taste.Profile.TopRated;
        for (var i = 0; i < taste.SeedKeys.Count && !IsFull(); i++)
        {
            var seed = taste.SeedKeys[i];
            var reason = i < topRated.Count
                ? $"Because you rated {topRated[i].Title} {topRated[i].Rating}/10."
                : PopularReason;
            Accumulate(
                await SafeFetchAsync(() => tmdb.GetRecommendationsAsync(seed.Media, seed.TmdbId, cancellationToken)),
                reason);
        }

        // 2) "Popular in your favorite genres." One discover per media the user engages with; the top favorite
        //    genre names the reason (the discover result mixes the favorite genres, so the leading one is honest).
        if (taste.FavoriteGenreTmdbIds.Count > 0)
        {
            var genreReason = taste.Profile.FavoriteGenres.Count > 0
                ? $"Popular in {taste.Profile.FavoriteGenres[0]}."
                : PopularReason;
            foreach (var media in presentMedia)
            {
                if (IsFull())
                {
                    break;
                }

                Accumulate(
                    await SafeFetchAsync(() => tmdb.DiscoverByGenreAsync(media, taste.FavoriteGenreTmdbIds, cancellationToken)),
                    genreReason);
            }
        }

        // 3) Global-popular top-up — also the entire set for a brand-new user (steps 1–2 produced nothing).
        foreach (var media in presentMedia)
        {
            if (IsFull())
            {
                break;
            }

            Accumulate(await SafeFetchAsync(() => tmdb.GetPopularAsync(media, cancellationToken)), PopularReason);
        }

        foreach (var media in presentMedia)
        {
            if (IsFull())
            {
                break;
            }

            Accumulate(await SafeFetchAsync(() => tmdb.GetTopRatedAsync(media, cancellationToken)), PopularReason);
        }

        return new RecommendationSet
        {
            Picks = picks,
            Source = RecommendationSource.Heuristic,
            GeneratedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };
    }
}
