using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Catalog;

/// <summary>
/// Resolves a TMDB <c>(TmdbId, MediaType)</c> pair to Cinora's internal <see cref="Movie.Id"/> using the
/// unique catalog index, or <c>null</c> when the title has not been first-touch persisted yet. It is the
/// read-only counterpart to <c>EnsureTitleCachedCommand</c>: public surfaces that address a title by its TMDB
/// coordinates (the Details reviews list) use it to reach the internal id WITHOUT triggering a TMDB fetch or a
/// write — a not-yet-cached title simply has no local reviews.
/// </summary>
/// <param name="TmdbId">The TMDB identifier of the title.</param>
/// <param name="MediaType">Whether the title is a movie or a series.</param>
public sealed record GetCachedMovieIdQuery(int TmdbId, MediaType MediaType) : IRequest<Guid?>;

/// <summary>Handles <see cref="GetCachedMovieIdQuery"/> with one indexed <c>AsNoTracking</c> scalar read.</summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
public sealed class GetCachedMovieIdQueryHandler(IAppDbContext db)
    : IRequestHandler<GetCachedMovieIdQuery, Guid?>
{
    /// <inheritdoc />
    public Task<Guid?> Handle(GetCachedMovieIdQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return db.Movies
            .AsNoTracking()
            .Where(movie => movie.TmdbId == request.TmdbId && movie.MediaType == request.MediaType)
            .Select(movie => (Guid?)movie.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
