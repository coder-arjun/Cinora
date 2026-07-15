namespace Cinora.Application.Features.Discovery;

/// <summary>
/// Which curated TMDB rail a Home-feed slot renders. Kept as its own type (not a per-request enum) because
/// it is a shared input: the <see cref="GetTitleRailQuery"/> switches on it and the Web shell configures a
/// rail slot with it. The numeric values are pinned so a rail can be referenced stably in a query string
/// (<c>/discover/rail?kind=Trending</c>) and any future persisted preference.
/// </summary>
public enum RailKind
{
    /// <summary>The current weekly trending titles.</summary>
    Trending = 0,

    /// <summary>The currently popular titles.</summary>
    Popular = 1,

    /// <summary>The all-time top-rated titles.</summary>
    TopRated = 2,
}
