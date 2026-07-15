using Cinora.Application.Features.Discovery;
using Cinora.Domain.Enums;

namespace Cinora.Web.ViewModels.Discovery;

/// <summary>
/// Composes a CQRS-pure <see cref="RailVm"/> (from the <c>ITmdbClient</c>-only rail query) with the current
/// user's watchlist statuses for the rail's titles (§5.3, ADR 0014). The controller resolves the batch
/// <c>GetWatchlistStatusMapQuery</c> ONCE for authenticated users, then hands the rail + map to the
/// <c>_Rail</c> partial so each card can render its <c>_WatchlistControl</c> without an N+1. Anonymous requests
/// get an empty map and the rail partial stays cacheable/pure.
/// </summary>
/// <param name="Rail">The pure rail read model.</param>
/// <param name="Statuses">TMDB id → the current user's status for that title (absent = not in list).</param>
/// <param name="IsAuthenticated">Whether the viewer is signed in (drives write control vs sign-in affordance).</param>
public sealed record RailViewModel(
    RailVm Rail,
    IReadOnlyDictionary<int, WatchlistStatus> Statuses,
    bool IsAuthenticated);

/// <summary>
/// The search equivalent of <see cref="RailViewModel"/>: a CQRS-pure <see cref="SearchResultsVm"/> composed
/// with the current user's watchlist statuses for the page's titles (resolved once per page via the batch
/// <c>GetWatchlistStatusMapQuery</c>, authenticated only — §5.3).
/// </summary>
/// <param name="Results">The pure search-results read model (one page of cards).</param>
/// <param name="Statuses">TMDB id → the current user's status for that title (absent = not in list).</param>
/// <param name="IsAuthenticated">Whether the viewer is signed in (drives write control vs sign-in affordance).</param>
public sealed record SearchResultsViewModel(
    SearchResultsVm Results,
    IReadOnlyDictionary<int, WatchlistStatus> Statuses,
    bool IsAuthenticated);
