using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.MovieLikes;

/// <summary>
/// Resolves the love state (count + whether-I-loved) for a page of title cards in ONE batch — keyed by TMDB id, so
/// each poster renders its love button without an N+1 (mirrors the watchlist status map, ADR 0014). Titles not yet
/// first-touch-persisted simply have no <c>Movie</c> row and are absent from the map (the card renders count 0 /
/// not-loved). The viewer is server-resolved; callers only invoke it when authenticated.
/// </summary>
/// <param name="TmdbIds">The TMDB ids of the cards on the page.</param>
/// <param name="Media">The media type shared by the page's cards.</param>
public sealed record GetMovieLikeMapQuery(IReadOnlyList<int> TmdbIds, MediaType Media)
    : IRequest<IReadOnlyDictionary<int, MovieLikeVm>>;

/// <summary>Handles <see cref="GetMovieLikeMapQuery"/> with three set-based reads (no N+1). Read-only (CQRS-pure).</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved viewer (ADR 0009).</param>
public sealed class GetMovieLikeMapQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetMovieLikeMapQuery, IReadOnlyDictionary<int, MovieLikeVm>>
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, MovieLikeVm>> Handle(
        GetMovieLikeMapQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.TmdbIds.Count == 0)
        {
            return new Dictionary<int, MovieLikeVm>();
        }

        var me = currentUser.GetRequiredUserId();

        // Resolve the on-page TMDB ids to the cached internal movies (only persisted titles can have loves).
        var movies = await db.Movies.AsNoTracking()
            .Where(movie => movie.MediaType == request.Media && request.TmdbIds.Contains(movie.TmdbId))
            .Select(movie => new { movie.Id, movie.TmdbId })
            .ToListAsync(cancellationToken);
        if (movies.Count == 0)
        {
            return new Dictionary<int, MovieLikeVm>();
        }

        var movieIds = movies.Select(movie => movie.Id).ToList();

        var counts = await db.MovieLikes.AsNoTracking()
            .Where(like => movieIds.Contains(like.MovieId))
            .GroupBy(like => like.MovieId)
            .Select(group => new { MovieId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.MovieId, row => row.Count, cancellationToken);

        var myLikes = (await db.MovieLikes.AsNoTracking()
            .Where(like => like.UserId == me && movieIds.Contains(like.MovieId))
            .Select(like => like.MovieId)
            .ToListAsync(cancellationToken)).ToHashSet();

        var map = new Dictionary<int, MovieLikeVm>(movies.Count);
        foreach (var movie in movies)
        {
            map[movie.TmdbId] = new MovieLikeVm(myLikes.Contains(movie.Id), counts.GetValueOrDefault(movie.Id, 0));
        }

        return map;
    }
}
