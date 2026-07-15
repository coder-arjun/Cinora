namespace Cinora.Domain.Enums;

/// <summary>Where a title sits in a user's <see cref="Entities.Watchlist"/>.</summary>
public enum WatchlistStatus
{
    /// <summary>The user intends to watch the title.</summary>
    PlanToWatch = 0,

    /// <summary>The user is currently watching the title.</summary>
    Watching = 1,

    /// <summary>The user has finished watching the title.</summary>
    Watched = 2,
}
