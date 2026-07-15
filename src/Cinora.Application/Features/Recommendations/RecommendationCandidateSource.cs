using Cinora.Application.Common.Ai;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Tmdb;
using Cinora.Domain.Enums;

namespace Cinora.Application.Features.Recommendations;

/// <summary>
/// The Application implementation of <see cref="IRecommendationCandidateSource"/> over <see cref="ITmdbClient"/>
/// (Phase 5 design §5.3, ADR 0017 §1). It grounds recommendation candidates in real TMDB titles: a
/// "more like your highest-rated" fan-out per seed, a "in your favorite genres" discover per media the user
/// engages with, and a popular/top-rated top-up when the pool is thin. The pool is deduplicated by
/// <c>(TmdbId, Media)</c> in first-seen order, the seen-exclusion set is removed, and the result is capped to
/// <see cref="IRecommendationPolicy.MaxCandidates"/>. Genre ids are resolved to names via ONE cached
/// <c>GetGenresAsync</c> call per media (never per-candidate); an unknown id is dropped from that title's genres
/// but never drops the title. Returns Application <see cref="CandidateTitle"/> records only — no TMDB DTO leaks.
/// </summary>
public sealed class RecommendationCandidateSource(ITmdbClient tmdb, IRecommendationPolicy policy)
    : IRecommendationCandidateSource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<CandidateTitle>> GenerateAsync(UserTasteResult taste, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taste);

        var maxCandidates = policy.MaxCandidates;

        // The media types the user actually engages with (from their seen titles); default to Movie if unknown.
        var presentMedia = taste.SeenExclusions.Select(key => key.Media).Distinct().ToList();
        if (presentMedia.Count == 0)
        {
            presentMedia = [MediaType.Movie];
        }

        var pool = new List<TmdbTitleSummary>();
        var keys = new HashSet<(int TmdbId, MediaType Media)>();

        void Accumulate(IReadOnlyList<TmdbTitleSummary> items)
        {
            foreach (var item in items)
            {
                var key = (item.TmdbId, item.MediaType);
                if (taste.SeenExclusions.Contains(key) || !keys.Add(key))
                {
                    continue; // already seen by the user, or already in the pool
                }

                pool.Add(item);
            }
        }

        // 1) More like your highest-rated: TMDB /{media}/{id}/recommendations for each seed.
        foreach (var seed in taste.SeedKeys)
        {
            Accumulate(await tmdb.GetRecommendationsAsync(seed.Media, seed.TmdbId, cancellationToken));
        }

        // 2) In your favorite genres: TMDB /discover per media (empty genres → the client returns []).
        foreach (var media in presentMedia)
        {
            Accumulate(await tmdb.DiscoverByGenreAsync(media, taste.FavoriteGenreTmdbIds, cancellationToken));
        }

        // 3) Top-up a thin fan-out from popular, then top-rated, until the pool reaches the cap.
        if (pool.Count < maxCandidates)
        {
            foreach (var media in presentMedia)
            {
                if (pool.Count >= maxCandidates)
                {
                    break;
                }

                Accumulate(await tmdb.GetPopularAsync(media, cancellationToken));
            }
        }

        if (pool.Count < maxCandidates)
        {
            foreach (var media in presentMedia)
            {
                if (pool.Count >= maxCandidates)
                {
                    break;
                }

                Accumulate(await tmdb.GetTopRatedAsync(media, cancellationToken));
            }
        }

        // 4) Cap to the configured maximum (first-seen order is preserved from the fan-out).
        var capped = pool.Count > maxCandidates ? pool.GetRange(0, maxCandidates) : pool;

        // Resolve genre ids to names via one cached genre-list read per media present in the capped pool.
        var genreNamesByMedia = new Dictionary<MediaType, IReadOnlyDictionary<int, string>>();
        foreach (var media in capped.Select(candidate => candidate.MediaType).Distinct())
        {
            var genres = await tmdb.GetGenresAsync(media, cancellationToken);
            genreNamesByMedia[media] = genres
                .GroupBy(genre => genre.TmdbGenreId)
                .ToDictionary(group => group.Key, group => group.First().Name);
        }

        return capped
            .Select(summary => ToCandidate(summary, genreNamesByMedia[summary.MediaType]))
            .ToList();
    }

    private static CandidateTitle ToCandidate(TmdbTitleSummary summary, IReadOnlyDictionary<int, string> genreNames)
    {
        var names = new List<string>(summary.GenreIds.Count);
        foreach (var genreId in summary.GenreIds)
        {
            // Unknown/unmapped genre id → drop that genre, keep the candidate (ADR 0017 §5.3).
            if (genreNames.TryGetValue(genreId, out var name))
            {
                names.Add(name);
            }
        }

        return new CandidateTitle
        {
            TmdbId = summary.TmdbId,
            Media = summary.MediaType,
            Title = summary.Title,
            Year = summary.ReleaseDate?.Year,
            PosterPath = summary.PosterPath,
            Genres = names,
        };
    }
}
