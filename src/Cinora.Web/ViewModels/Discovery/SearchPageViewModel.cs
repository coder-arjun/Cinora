using Cinora.Application.Features.Discovery;
using Cinora.Application.Features.MovieLikes;
using Cinora.Domain.Enums;

namespace Cinora.Web.ViewModels.Discovery;

/// <summary>
/// The presentation model for the full Search page (<c>GET /discover/search</c>, Milestone 2.4). It carries
/// the echoed query + media (so the input and the Alpine/HTMX region can seed themselves for deep-links and
/// the no-JS baseline) and the server-rendered first page of <see cref="Results"/> when the query was valid.
/// When <see cref="Results"/> is <c>null</c> (no query yet, or too short) the page shows the idle
/// <c>_SearchPrompt</c> instead — this is the no-JS + shareable-URL baseline; with JS the Alpine
/// <c>search</c> component swaps the <c>_SearchResults</c> partial in place as the user types.
/// </summary>
/// <param name="Query">The trimmed query echoed into the input (empty when none was supplied).</param>
/// <param name="Media">The media type to search (defaults to Movie; drives the region's <c>data-media</c>).</param>
/// <param name="Results">The server-rendered page-1 results, or <c>null</c> to show the idle prompt.</param>
/// <param name="Statuses">The current user's watchlist statuses for the inline page-1 titles, keyed by
/// (TMDB id, media) since unified search mixes movies + series; empty for anonymous or no results.</param>
/// <param name="Likes">The love state (count + whether-I-loved) for the inline page-1 titles, keyed by
/// (TMDB id, media); empty for anonymous.</param>
/// <param name="IsAuthenticated">Whether the viewer is signed in (drives the cards' watchlist control).</param>
public sealed record SearchPageViewModel(
    string Query,
    MediaType Media,
    SearchResultsVm? Results,
    IReadOnlyDictionary<(int TmdbId, MediaType Media), WatchlistStatus> Statuses,
    IReadOnlyDictionary<(int TmdbId, MediaType Media), MovieLikeVm> Likes,
    bool IsAuthenticated);
