using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Watchlist;

/// <summary>
/// The single-title watchlist probe (used by the Details page): resolves the current user's status for one
/// title, or <c>null</c> when the title is not in their list or the request is anonymous. Keyed on
/// <c>(currentUser, MovieId)</c> — a user can only ever read their own entry (ADR 0009). A read-only
/// <c>AsNoTracking</c> scalar over the unique <c>(UserId, MovieId)</c> index.
/// </summary>
/// <param name="MovieId">The internal <c>Movie.Id</c> of the title to probe.</param>
public sealed record GetWatchlistStatusQuery(Guid MovieId) : IRequest<WatchlistStatus?>;

/// <summary>
/// Handles <see cref="GetWatchlistStatusQuery"/> with one indexed <c>AsNoTracking</c> scalar read. An
/// anonymous caller short-circuits to <c>null</c> (not in list) without touching the database.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved current user (may be anonymous → <c>null</c>).</param>
public sealed class GetWatchlistStatusQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetWatchlistStatusQuery, WatchlistStatus?>
{
    /// <inheritdoc />
    public async Task<WatchlistStatus?> Handle(
        GetWatchlistStatusQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (currentUser.UserId is not { } userId)
        {
            return null; // Anonymous → the title is not in "your" list.
        }

        return await db.Watchlists
            .AsNoTracking()
            .Where(entry => entry.UserId == userId && entry.MovieId == request.MovieId)
            .Select(entry => (WatchlistStatus?)entry.Status)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
