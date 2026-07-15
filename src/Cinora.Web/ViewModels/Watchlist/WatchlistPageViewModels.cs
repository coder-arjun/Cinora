using Cinora.Application.Features.Watchlist;
using Cinora.Domain.Enums;

namespace Cinora.Web.ViewModels.Watchlist;

/// <summary>
/// The model for the <c>/watchlist</c> page shell (Milestone 4.4, §6.2). It composes the CQRS-pure
/// <see cref="WatchlistPageVm"/> (page 1 of the current user's watchlist) with the per-status
/// <see cref="WatchlistCountsVm"/> that drives the filter chips, plus the active status filter and sort so the
/// chips/sort control render their current state and the load-more sentinel can carry them forward. The page is
/// <c>[Authorize]</c>, so the viewer is always signed in — each card renders the authenticated
/// <c>_WatchlistControl</c> (this mirrors the <c>SearchResultsViewModel</c> composition shape).
/// </summary>
/// <param name="Page">Page 1 of the current user's watchlist (items + keyset cursor + <c>HasMore</c>).</param>
/// <param name="Counts">The per-status counts behind the filter chips (Plan · Watching · Watched · All).</param>
/// <param name="ActiveFilter">The status the page is scoped to, or <c>null</c> for "All".</param>
/// <param name="Sort">The active sort order (round-tripped into the chip and load-more URLs).</param>
public sealed record WatchlistPageViewModel(
    WatchlistPageVm Page,
    WatchlistCountsVm Counts,
    WatchlistStatus? ActiveFilter,
    WatchlistSort Sort);

/// <summary>
/// The model for the <c>_WatchlistPage</c> load-more fragment (Milestone 4.4, §6.2) — the append slice returned
/// by the keyset sentinel's <c>hx-get</c>. It carries only the next <see cref="WatchlistPageVm"/> plus the
/// active filter/sort needed to build the next sentinel's URL (no counts — the chips are already rendered on the
/// shell). Mirrors the feed's <c>_FeedPage</c> fragment grammar.
/// </summary>
/// <param name="Page">The next keyset page of watchlist items (+ cursor + <c>HasMore</c>).</param>
/// <param name="ActiveFilter">The status the page is scoped to, or <c>null</c> for "All".</param>
/// <param name="Sort">The active sort order (round-tripped into the next load-more URL).</param>
public sealed record WatchlistLoadMoreViewModel(
    WatchlistPageVm Page,
    WatchlistStatus? ActiveFilter,
    WatchlistSort Sort);

/// <summary>
/// The model for the <c>_WatchlistError</c> partial (Milestone 4.4): the on-brand "couldn't load more of your
/// watchlist" state served with HTTP 200 (so HTMX swaps it in place of the shimmering load-more sentinel — it
/// never swaps a non-2xx, so a 500 would strand the sentinel shimmering forever) when a lazy watchlist page read
/// fails after the query pipeline. Its Retry control re-fires the exact same lazy <c>hx-get</c>. Mirrors the
/// feed's <c>FeedErrorVm</c> grammar.
/// </summary>
/// <param name="RetryUrl">The URL the Retry control re-GETs — the same lazy watchlist page that just failed.</param>
public sealed record WatchlistErrorVm(string RetryUrl);
