using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Discovery;

/// <summary>
/// Returns the Cinora community's most-loved movies — the titles with the most poster "loves"
/// (<c>MovieLike</c> rows), highest first. This is the data source behind the Discover "Most Loved"
/// rail (feature #4: surface/sort titles by likes); the existing Top-Rated TMDB rails already cover
/// "by rating". Unlike the TMDB rails this reads Cinora's OWN data, so it lives over
/// <see cref="IAppDbContext"/> rather than <c>ITmdbClient</c> and returns the shared <see cref="RailVm"/>
/// so the standard <c>_Rail</c> partial renders it unchanged.
/// </summary>
/// <remarks>
/// Scoped to movies (<see cref="MediaType.Movie"/>) on purpose: the card love/watchlist batch maps the
/// controller resolves are keyed by TMDB id alone, and a movie and a series can share a TMDB id, so a
/// single-media rail keeps those maps collision-free. Two set-based reads (grouped counts, then the movie
/// rows) — no N+1. An app with no loves yet yields an empty rail, which <c>_Rail</c> renders as a quiet
/// empty state (never an error).
/// </remarks>
/// <param name="Take">How many titles to return (clamped 1..30).</param>
public sealed record GetMostLovedTitlesQuery(int Take = 20) : IRequest<RailVm>;

/// <summary>
/// Handles <see cref="GetMostLovedTitlesQuery"/>: ranks movie ids by their love count, fetches those movie
/// rows, and projects them to <see cref="TitleCardVm"/> in rank order.
/// </summary>
/// <param name="db">The Application-owned persistence abstraction (read-only here).</param>
public sealed class GetMostLovedTitlesQueryHandler(IAppDbContext db)
    : IRequestHandler<GetMostLovedTitlesQuery, RailVm>
{
    /// <inheritdoc />
    public async Task<RailVm> Handle(GetMostLovedTitlesQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var take = Math.Clamp(request.Take, 1, 30);

        // 1) Rank movie ids by love count (set-based GROUP BY … ORDER BY COUNT DESC … TOP n).
        var ranked = await db.MovieLikes
            .AsNoTracking()
            .GroupBy(like => like.MovieId)
            .Select(group => new { MovieId = group.Key, Count = group.Count() })
            .OrderByDescending(row => row.Count)
            .Take(take)
            .ToListAsync(cancellationToken);

        if (ranked.Count == 0)
        {
            return new RailVm(RailKind.TopRated, MediaType.Movie, []);
        }

        // 2) Fetch just those movie rows (movies only — see remarks), then order in memory by the rank above.
        var ids = ranked.Select(row => row.MovieId).ToArray();
        var rank = ranked.Select((row, index) => (row.MovieId, index)).ToDictionary(t => t.MovieId, t => t.index);

        var movies = await db.Movies
            .AsNoTracking()
            .Where(movie => ids.Contains(movie.Id) && movie.MediaType == MediaType.Movie)
            .Select(movie => new
            {
                movie.Id,
                movie.TmdbId,
                movie.MediaType,
                movie.Title,
                movie.ReleaseDate,
                movie.PosterPath,
            })
            .ToListAsync(cancellationToken);

        var cards = movies
            .OrderBy(movie => rank[movie.Id])
            .Select(movie => new TitleCardVm
            {
                TmdbId = movie.TmdbId,
                MediaType = movie.MediaType,
                Title = movie.Title,
                ReleaseYear = movie.ReleaseDate is { } date ? date.Year : null,
                PosterPath = movie.PosterPath,
                VoteAverage = 0, // Cinora doesn't cache the TMDB rating; the love count is this rail's signal.
            })
            .ToArray();

        // RailKind here is cosmetic — _Rail renders Items only and never reads Kind.
        return new RailVm(RailKind.TopRated, MediaType.Movie, cards);
    }
}
