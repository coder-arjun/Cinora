using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.MovieLikes;

/// <summary>
/// Toggles the current user's "love" for a title (the poster love button). Adds the like if absent, removes it if
/// present, and returns the new state + total count. The actor is server-resolved (ADR 0009); the caller supplies
/// the internal <c>MovieId</c> (resolved via the read-then-EnsureTitleCached orchestration, ADR 0007/0008).
/// </summary>
/// <param name="MovieId">The internal id of the loved title (NOT the TMDB id).</param>
public sealed record ToggleMovieLikeCommand(Guid MovieId) : IRequest<MovieLikeVm>;

/// <summary>Handles <see cref="ToggleMovieLikeCommand"/>.</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved acting user (ADR 0009).</param>
public sealed class ToggleMovieLikeCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<ToggleMovieLikeCommand, MovieLikeVm>
{
    /// <inheritdoc />
    public async Task<MovieLikeVm> Handle(ToggleMovieLikeCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();

        var existing = await db.MovieLikes
            .FirstOrDefaultAsync(like => like.MovieId == request.MovieId && like.UserId == me, cancellationToken);

        bool liked;
        if (existing is null)
        {
            db.MovieLikes.Add(MovieLike.Create(request.MovieId, me));
            liked = true;
        }
        else
        {
            db.MovieLikes.Remove(existing);
            liked = false;
        }

        await db.SaveChangesAsync(cancellationToken);

        var count = await db.MovieLikes.AsNoTracking()
            .CountAsync(like => like.MovieId == request.MovieId, cancellationToken);

        return new MovieLikeVm(liked, count);
    }
}
