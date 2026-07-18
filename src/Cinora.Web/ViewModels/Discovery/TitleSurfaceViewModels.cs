using Cinora.Application.Features.Discovery;
using Cinora.Application.Features.MovieLikes;
using Cinora.Domain.Enums;

namespace Cinora.Web.ViewModels.Discovery;

/// <summary>
/// Composes a CQRS-pure <see cref="RailVm"/> (from the <c>ITmdbClient</c>-only rail query) with the current
/// user's watchlist statuses AND love state for the rail's titles (§5.3, ADR 0014). The controller resolves the
/// batch maps ONCE for authenticated users, then hands the rail + maps to the <c>_Rail</c> partial so each card
/// can render its <c>_WatchlistControl</c> + <c>_LoveButton</c> without an N+1. Anonymous requests get empty maps
/// and the rail partial stays cacheable/pure.
/// </summary>
/// <param name="Rail">The pure rail read model.</param>
/// <param name="Statuses">TMDB id → the current user's watchlist status for that title (absent = not in list).</param>
/// <param name="Likes">TMDB id → the title's love state (count + whether I loved it); absent = count 0, not loved.</param>
/// <param name="IsAuthenticated">Whether the viewer is signed in (drives write controls vs sign-in affordance).</param>
public sealed record RailViewModel(
    RailVm Rail,
    IReadOnlyDictionary<int, WatchlistStatus> Statuses,
    IReadOnlyDictionary<int, MovieLikeVm> Likes,
    bool IsAuthenticated);

/// <summary>
/// The search equivalent of <see cref="RailViewModel"/>: a CQRS-pure <see cref="SearchResultsVm"/> composed with
/// the current user's watchlist statuses and love state for the page's titles (resolved once per page, authed).
/// </summary>
/// <remarks>
/// Unlike the single-media rail maps, the maps here are keyed by <c>(TMDB id, media type)</c> because unified
/// search mixes movies and series in one page — and a movie id and a series id can be the same integer, so a
/// plain int key could collide. The view looks up with <c>(card.TmdbId, card.MediaType)</c>.
/// </remarks>
/// <param name="Results">The pure search-results read model (one page of cards).</param>
/// <param name="Statuses">(TMDB id, media) → the current user's watchlist status (absent = not in list).</param>
/// <param name="Likes">(TMDB id, media) → the title's love state (count + whether I loved it); absent = 0/not loved.</param>
/// <param name="IsAuthenticated">Whether the viewer is signed in (drives write controls vs sign-in affordance).</param>
public sealed record SearchResultsViewModel(
    SearchResultsVm Results,
    IReadOnlyDictionary<(int TmdbId, MediaType Media), WatchlistStatus> Statuses,
    IReadOnlyDictionary<(int TmdbId, MediaType Media), MovieLikeVm> Likes,
    bool IsAuthenticated);

/// <summary>
/// The model for the shared <c>_LoveButton</c> partial: a title's love button + count for one surface (poster card
/// or Details). It toggles via <c>POST /discover/title/{media}/{tmdbId}/like</c> and swaps itself.
/// </summary>
/// <param name="TmdbId">The title's TMDB id.</param>
/// <param name="Media">The title's media type.</param>
/// <param name="Liked">Whether the current user has loved this title.</param>
/// <param name="Count">The total number of loves.</param>
/// <param name="IsAuthenticated">Whether the viewer is signed in (an anonymous viewer sees a read-only count).</param>
/// <param name="OnDetails">Whether this renders on the Details page (larger) vs a poster overlay (compact).</param>
public sealed record LoveButtonVm(
    int TmdbId,
    MediaType Media,
    bool Liked,
    int Count,
    bool IsAuthenticated,
    bool OnDetails = false);
