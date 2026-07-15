using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Enums;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Watchlist;

/// <summary>
/// Adds a title to the current user's watchlist, or updates its status if it is already there — a single
/// <b>upsert</b>. The acting user is resolved server-side from <see cref="ICurrentUser"/>; the command carries
/// only the internal <paramref name="MovieId"/> (the resource), never a user id, so a caller can only ever
/// touch their own <c>(UserId, MovieId)</c> row (ADR 0009, §5.1). The title must already be first-touch
/// persisted (the controller runs <c>EnsureTitleCachedCommand</c> first — ADR 0008/0014); this command does
/// not resolve TMDB coordinates.
/// </summary>
/// <param name="MovieId">The internal <c>Movie.Id</c> of the tracked title (resolved by the caller).</param>
/// <param name="Status">The viewing status to set (Plan to Watch / Watching / Watched).</param>
public sealed record SetWatchlistStatusCommand(Guid MovieId, WatchlistStatus Status)
    : IRequest<SetWatchlistResult>;

/// <summary>The outcome of a <see cref="SetWatchlistStatusCommand"/>.</summary>
/// <param name="Status">The status now recorded for the title.</param>
/// <param name="WasAdded">Whether a new entry was created (<c>true</c>) or an existing one updated (<c>false</c>).</param>
public sealed record SetWatchlistResult(WatchlistStatus Status, bool WasAdded);

/// <summary>
/// Validates <see cref="SetWatchlistStatusCommand"/> for a friendly 400: the status must be a defined
/// <see cref="WatchlistStatus"/>. This is defense-in-depth only — the authoritative status invariant stays in
/// the domain (<see cref="WatchlistStatus"/> / <c>Watchlist.Add</c> / <c>Watchlist.ChangeStatus</c>).
/// </summary>
public sealed class SetWatchlistStatusCommandValidator : AbstractValidator<SetWatchlistStatusCommand>
{
    /// <summary>Configures the rules for <see cref="SetWatchlistStatusCommand"/>.</summary>
    public SetWatchlistStatusCommandValidator()
    {
        RuleFor(command => command.Status)
            .Must(status => Enum.IsDefined(status))
            .WithMessage("The watchlist status is not a recognised value.");
    }
}

/// <summary>
/// Handles <see cref="SetWatchlistStatusCommand"/> as an upsert keyed on <c>(currentUser, MovieId)</c>. It
/// loads the tracked entry: present → <c>ChangeStatus</c>; absent → <c>Watchlist.Add</c>. A concurrent insert
/// that trips the unique <c>(UserId, MovieId)</c> index is caught, the staged insert is discarded, and the
/// winning row is re-probed and updated — the same reconcile-on-race pattern the review and catalog seams use
/// (ADR 0008). Any other <see cref="DbUpdateException"/> (e.g. a bad <c>MovieId</c> foreign key) is not a
/// watchlist race and is rethrown to surface as a fault.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (§5.1, ADR 0009).</param>
public sealed class SetWatchlistStatusCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<SetWatchlistStatusCommand, SetWatchlistResult>
{
    /// <inheritdoc />
    public async Task<SetWatchlistResult> Handle(
        SetWatchlistStatusCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.GetRequiredUserId();

        // Fast path: an existing entry is loaded tracked and its status changed in place (the common re-toggle).
        var existing = await FindTrackedEntryAsync(userId, request.MovieId, cancellationToken);
        if (existing is not null)
        {
            existing.ChangeStatus(request.Status);
            await db.SaveChangesAsync(cancellationToken);
            return new SetWatchlistResult(request.Status, WasAdded: false);
        }

        db.Watchlists.Add(Cinora.Domain.Entities.Watchlist.Add(userId, request.MovieId, request.Status));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new SetWatchlistResult(request.Status, WasAdded: true);
        }
        catch (DbUpdateException)
        {
            // Lost a race on the unique (UserId, MovieId) index. Discard our now-conflicting Added row so it
            // cannot re-flush, then reconcile: reload the winner and update it. If nothing resolves, the failure
            // was NOT a watchlist race (e.g. a FK violation) — rethrow so it surfaces rather than masking a fault.
            db.DiscardPendingChanges();

            var raced = await FindTrackedEntryAsync(userId, request.MovieId, cancellationToken);
            if (raced is not null)
            {
                raced.ChangeStatus(request.Status);
                await db.SaveChangesAsync(cancellationToken);
                return new SetWatchlistResult(request.Status, WasAdded: false);
            }

            throw;
        }
    }

    // Loads the user's TRACKED entry for the title (or null) so a domain method can mutate it and be saved.
    private Task<Cinora.Domain.Entities.Watchlist?> FindTrackedEntryAsync(
        Guid userId, Guid movieId, CancellationToken cancellationToken) =>
        db.Watchlists.FirstOrDefaultAsync(
            entry => entry.UserId == userId && entry.MovieId == movieId, cancellationToken);
}
