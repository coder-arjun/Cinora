using Cinora.Application.Common.Ai;
using Cinora.Application.Common.Interfaces;
using Cinora.Domain.Enums;
using Cinora.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Recommendations;

/// <summary>
/// The Application implementation of <see cref="IUserTasteProfileBuilder"/> over <see cref="IAppDbContext"/>
/// (Phase 5 design §5.1). It projects a user's taste with a small, bounded set of <c>AsNoTracking().Select(...)</c>
/// reads — no per-title round-trips, no N+1 (the genre id→name resolution for the rated titles is ONE grouped
/// read). Every list is capped through <see cref="IRecommendationPolicy"/> so the profile stays small (the §8
/// token controls) and PII-free (the §12 hygiene rule — titles/genres/ratings only, never the user's id/name).
/// A user with no reviews and no watchlist is a cold-start signal: <see cref="UserTasteResult.HasSignal"/> is
/// false and the genre reads are skipped.
/// </summary>
public sealed class UserTasteProfileBuilder(IAppDbContext db, IRecommendationPolicy policy)
    : IUserTasteProfileBuilder
{
    /// <inheritdoc />
    public async Task<UserTasteResult> BuildAsync(Guid userId, CancellationToken cancellationToken)
    {
        // 1) Top-rated titles (bounded): the user's reviews joined to their movies, best score first then most
        //    recent. Ordering/projecting the value-converted Rating maps to its underlying int column.
        var ratedRows = await (
                from review in db.Reviews.AsNoTracking()
                where review.UserId == userId
                join movie in db.Movies on review.MovieId equals movie.Id
                orderby review.Rating descending, review.CreatedAtUtc descending
                select new RatedRow
                {
                    MovieId = movie.Id,
                    TmdbId = movie.TmdbId,
                    Media = movie.MediaType,
                    Title = movie.Title,
                    ReleaseDate = movie.ReleaseDate,
                    Rating = review.Rating,
                })
            .Take(policy.MaxTopRated)
            .ToListAsync(cancellationToken);

        // 2) Watchlist interest (bounded): plan-to-watch / watching titles, most recently added first (a
        //    positive-intent signal; already-watched titles are excluded — they are "seen", not "want to see").
        var watchlistTitles = await (
                from entry in db.Watchlists.AsNoTracking()
                where entry.UserId == userId
                      && (entry.Status == WatchlistStatus.PlanToWatch || entry.Status == WatchlistStatus.Watching)
                join movie in db.Movies on entry.MovieId equals movie.Id
                orderby entry.AddedAtUtc descending
                select movie.Title)
            .Take(policy.MaxWatchlistTitles)
            .ToListAsync(cancellationToken);

        // 3) Seen-exclusion set: every (TmdbId, Media) the user reviewed OR has any watchlist row for. One read
        //    (union of reviewed ∪ watchlisted movie ids, joined to Movies) gives the exclusions, the movie ids
        //    the genre-affinity read needs, and the cold-start signal.
        var reviewedMovieIds = db.Reviews.Where(review => review.UserId == userId).Select(review => review.MovieId);
        var watchlistedMovieIds = db.Watchlists.Where(entry => entry.UserId == userId).Select(entry => entry.MovieId);
        var seenMovies = await reviewedMovieIds
            .Union(watchlistedMovieIds)
            .Join(db.Movies, id => id, movie => movie.Id, (id, movie) => new SeenRow
            {
                MovieId = movie.Id,
                TmdbId = movie.TmdbId,
                Media = movie.MediaType,
            })
            .ToListAsync(cancellationToken);

        var seenExclusions = seenMovies.Select(seen => (seen.TmdbId, seen.Media)).ToHashSet();

        // Cold-start (no reviews AND no watchlist): a thin profile. Skip the genre reads and signal it.
        if (seenMovies.Count == 0)
        {
            return new UserTasteResult { Profile = new TasteProfile(), HasSignal = false };
        }

        var userMovieIds = seenMovies.Select(seen => seen.MovieId).ToList();

        // 4) Favorite genres by frequency across the user's rated + watchlisted movies (bounded).
        var topGenreCounts = await db.MovieGenres.AsNoTracking()
            .Where(link => userMovieIds.Contains(link.MovieId))
            .GroupBy(link => link.GenreId)
            .Select(group => new { GenreId = group.Key, Count = group.Count() })
            // ThenBy(GenreId) is a deterministic tiebreaker so genres tied on frequency select/order
            // reproducibly — a run-to-run stable profile (reproducible candidate fan-out, testable output). The
            // shipped skip-unchanged check is timestamp-based (the 5.3 job compares latest AIRecommendationHistory
            // vs latest activity; ADR 0018 amendment), so it does not depend on this determinism — but a stable
            // profile stays the prerequisite for the recorded future durable taste-hash refinement.
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.GenreId)
            .Take(policy.MaxFavoriteGenres)
            .ToListAsync(cancellationToken);

        var topGenreIds = topGenreCounts.Select(entry => entry.GenreId).ToList();
        var genreById = (await db.Genres.AsNoTracking()
                .Where(genre => topGenreIds.Contains(genre.Id))
                .Select(genre => new { genre.Id, genre.Name, genre.TmdbGenreId })
                .ToListAsync(cancellationToken))
            .ToDictionary(genre => genre.Id);

        // Preserve the frequency order established above when resolving names/ids.
        var favoriteGenreNames = new List<string>(topGenreCounts.Count);
        var favoriteGenreTmdbIds = new List<int>(topGenreCounts.Count);
        foreach (var genreCount in topGenreCounts)
        {
            if (genreById.TryGetValue(genreCount.GenreId, out var genre))
            {
                favoriteGenreNames.Add(genre.Name);
                favoriteGenreTmdbIds.Add(genre.TmdbGenreId);
            }
        }

        // 5) Genre names for the rated titles — ONE grouped read keyed by movie id (never per-title).
        var ratedMovieIds = ratedRows.Select(row => row.MovieId).ToList();
        var genreNamesByMovie = ratedMovieIds.Count == 0
            ? new Dictionary<Guid, List<string>>()
            : (await db.MovieGenres.AsNoTracking()
                    .Where(link => ratedMovieIds.Contains(link.MovieId))
                    .Join(db.Genres, link => link.GenreId, genre => genre.Id,
                        (link, genre) => new { link.MovieId, genre.Name })
                    .ToListAsync(cancellationToken))
                .GroupBy(row => row.MovieId)
                .ToDictionary(group => group.Key, group => group.Select(row => row.Name).ToList());

        var topRated = ratedRows
            .Select(row => new RatedTitle
            {
                Title = row.Title,
                Year = row.ReleaseDate?.Year,
                Rating = row.Rating.Value,
                Genres = genreNamesByMovie.TryGetValue(row.MovieId, out var names) ? names : new List<string>(),
            })
            .ToList();

        var seedKeys = ratedRows
            .Take(policy.MaxRecommendationSeeds)
            .Select(row => (row.TmdbId, row.Media))
            .ToList();

        return new UserTasteResult
        {
            Profile = new TasteProfile
            {
                TopRated = topRated,
                WatchlistTitles = watchlistTitles,
                FavoriteGenres = favoriteGenreNames,
            },
            SeedKeys = seedKeys,
            FavoriteGenreTmdbIds = favoriteGenreTmdbIds,
            SeenExclusions = seenExclusions,
            HasSignal = true,
        };
    }

    // The intermediate shape EF materializes for a rated title; the genre names are joined in a separate read.
    private sealed class RatedRow
    {
        public Guid MovieId { get; init; }

        public int TmdbId { get; init; }

        public MediaType Media { get; init; }

        public string Title { get; init; } = null!;

        public DateTime? ReleaseDate { get; init; }

        public Rating Rating { get; init; }
    }

    // The intermediate shape for a seen title (reviewed or watchlisted).
    private sealed class SeenRow
    {
        public Guid MovieId { get; init; }

        public int TmdbId { get; init; }

        public MediaType Media { get; init; }
    }
}
