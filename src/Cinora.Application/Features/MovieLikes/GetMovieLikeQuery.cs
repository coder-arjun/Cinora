using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.MovieLikes;

/// <summary>Reads the love state of a single title (count + whether the current user loved it) — for the Details page.</summary>
/// <param name="MovieId">The internal id of the title.</param>
public sealed record GetMovieLikeQuery(Guid MovieId) : IRequest<MovieLikeVm>;

/// <summary>Handles <see cref="GetMovieLikeQuery"/>. Read-only (CQRS-pure).</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved viewer (ADR 0009).</param>
public sealed class GetMovieLikeQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetMovieLikeQuery, MovieLikeVm>
{
    /// <inheritdoc />
    public async Task<MovieLikeVm> Handle(GetMovieLikeQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();

        var count = await db.MovieLikes.AsNoTracking()
            .CountAsync(like => like.MovieId == request.MovieId, cancellationToken);
        var liked = await db.MovieLikes.AsNoTracking()
            .AnyAsync(like => like.MovieId == request.MovieId && like.UserId == me, cancellationToken);

        return new MovieLikeVm(liked, count);
    }
}
