using Cinora.Domain.Enums;

namespace Cinora.Domain.Entities;

/// <summary>
/// An entry in a user's watchlist tracking a title and its viewing status. A user may hold at most
/// one entry per title — that uniqueness is enforced by EF Core in Milestone 1.2.
/// </summary>
public sealed class Watchlist
{
    private Watchlist()
    {
    }

    /// <summary>The unique identifier of the watchlist entry.</summary>
    public Guid Id { get; private set; }

    /// <summary>The identifier of the owning user.</summary>
    public Guid UserId { get; private set; }

    /// <summary>The identifier of the tracked title.</summary>
    public Guid MovieId { get; private set; }

    /// <summary>The current viewing status.</summary>
    public WatchlistStatus Status { get; private set; }

    /// <summary>The UTC instant the entry was added.</summary>
    public DateTime AddedAtUtc { get; private set; }

    /// <summary>The UTC instant the status last changed, or <c>null</c> if it has not changed since being added.</summary>
    public DateTime? UpdatedAtUtc { get; private set; }

    /// <summary>Adds a title to a user's watchlist.</summary>
    /// <param name="userId">The owning user's identifier.</param>
    /// <param name="movieId">The tracked title's identifier.</param>
    /// <param name="status">The initial viewing status.</param>
    /// <returns>A new <see cref="Watchlist"/> entry.</returns>
    public static Watchlist Add(Guid userId, Guid movieId, WatchlistStatus status) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MovieId = movieId,
            Status = status,
            AddedAtUtc = DateTime.UtcNow,
        };

    /// <summary>Changes the viewing status and records the update time.</summary>
    /// <param name="status">The new viewing status.</param>
    public void ChangeStatus(WatchlistStatus status)
    {
        Status = status;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
