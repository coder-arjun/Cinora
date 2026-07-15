using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Watchlist;

/// <summary>
/// Removes a title from the current user's watchlist. Keyed on <c>(currentUser, MovieId)</c> and
/// <b>idempotent</b>: if there is no such entry it is a no-op success (removing something already gone is not an
/// error). The acting user is resolved server-side from <see cref="ICurrentUser"/> (ADR 0009); the command
/// carries only the internal <paramref name="MovieId"/>, so a caller can only remove their own row.
/// </summary>
/// <param name="MovieId">The internal <c>Movie.Id</c> of the title to remove (resolved by the caller via
/// <c>GetCachedMovieIdQuery</c>).</param>
public sealed record RemoveFromWatchlistCommand(Guid MovieId) : IRequest<Unit>;

/// <summary>
/// Handles <see cref="RemoveFromWatchlistCommand"/>. It loads the user's tracked entry; a miss is a no-op
/// success (idempotent). A concurrent delete that removes the row first surfaces as a
/// <see cref="DbUpdateConcurrencyException"/> on save — that too is reconciled to success (the row is gone,
/// which is exactly what was asked), never a fault.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (§5.1, ADR 0009).</param>
public sealed class RemoveFromWatchlistCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<RemoveFromWatchlistCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(RemoveFromWatchlistCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.GetRequiredUserId();

        var entry = await db.Watchlists.FirstOrDefaultAsync(
            item => item.UserId == userId && item.MovieId == request.MovieId, cancellationToken);
        if (entry is null)
        {
            return Unit.Value; // Nothing to remove — idempotent no-op.
        }

        db.Watchlists.Remove(entry);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent request already deleted this row. The desired end state (no entry) is met, so treat
            // it as an idempotent success; discard the stale staged delete before returning.
            db.DiscardPendingChanges();
        }

        return Unit.Value;
    }
}
