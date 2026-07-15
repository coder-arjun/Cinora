using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Catalog;
using Cinora.Application.Features.Watchlist;
using Cinora.Domain.Enums;
using Cinora.Web.Infrastructure;
using Cinora.Web.ViewModels.Watchlist;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

namespace Cinora.Web.Controllers;

/// <summary>
/// The authenticated write surface for watchlist status control (Milestone 4.3): set (add/update) or remove a
/// title's status from ANY surface — Details, the discovery rails, and search — plus the reusable status
/// control fragment. The class is <see cref="AuthorizeAttribute"/> (fail-closed); every state-changing verb is
/// validated by the global <c>AutoValidateAntiforgeryToken</c> (HTMX sends the token via the
/// <c>RequestVerificationToken</c> header — no opt-out). It stays thin: it model-binds, orchestrates the
/// ADR-0008 seam (<c>EnsureTitleCachedCommand</c> → the watchlist command — never <c>ISender</c> inside a
/// handler), and returns the re-rendered <c>_WatchlistControl</c> partial or a redirect. Ownership is implicit
/// and server-resolved (ADR 0009): the handlers key on <c>ICurrentUser</c>, so no user id is ever bound.
/// </summary>
/// <param name="sender">The hand-rolled mediator used to dispatch the catalog + watchlist requests.</param>
/// <param name="logger">Logs a Warning when a lazy watchlist page read degrades to its retry partial (HTTP 200).</param>
[Authorize]
[Route("watchlist")]
public sealed partial class WatchlistController(ISender sender, ILogger<WatchlistController> logger) : Controller
{
    /// <summary>
    /// Sets (adds or updates) a title's watchlist status (<c>POST /watchlist</c>). It orchestrates the ADR-0008
    /// seam (§5.2): first-touch persist the title via <c>EnsureTitleCachedCommand</c> to obtain the internal
    /// <c>MovieId</c> (a title TMDB does not have → 404), then <see cref="SetWatchlistStatusCommand"/>. Under
    /// HTMX it returns the re-rendered <c>_WatchlistControl</c>; a no-JS post redirects back to the title.
    /// </summary>
    /// <param name="form">The bound set form (TMDB coordinates + status; no user id).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handlers.</param>
    /// <returns>The <c>_WatchlistControl</c> partial (HTMX) or a redirect (no-JS); <c>404</c> for an unknown title.</returns>
    [HttpPost("")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Set([FromForm] SetWatchlistForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (form.TmdbId <= 0 || !Enum.IsDefined(form.Media))
        {
            return NotFound();
        }

        var ensure = await sender.Send(new EnsureTitleCachedCommand(form.TmdbId, form.Media), cancellationToken);
        if (ensure.Outcome == EnsureTitleOutcome.NotFound || ensure.MovieId is not { } movieId)
        {
            return NotFound();
        }

        // The validator produces the friendly 400 for an out-of-range status (§5.1).
        var result = await sender.Send(
            new SetWatchlistStatusCommand(movieId, form.Status), cancellationToken);

        var control = new WatchlistControlVm(form.TmdbId, form.Media, result.Status, IsAuthenticated: true)
        {
            OnDetails = form.OnDetails,
        };

        if (IsHtmxRequest())
        {
            return PartialView("_WatchlistControl", control);
        }

        return RedirectToTitle(form.Media, form.TmdbId);
    }

    /// <summary>
    /// Removes a title from the current user's watchlist (<c>DELETE /watchlist</c>). It resolves the internal
    /// <c>MovieId</c> via <c>GetCachedMovieIdQuery</c> (no TMDB fetch — a not-yet-cached title has nothing to
    /// remove) then <see cref="RemoveFromWatchlistCommand"/> (idempotent). Returns the re-rendered
    /// <c>_WatchlistControl</c> — now the "Add to watchlist" state.
    /// </summary>
    /// <param name="tmdbId">The title's TMDB id.</param>
    /// <param name="media">The title's media type.</param>
    /// <param name="onDetails">Whether the control is on the Details page (round-tripped for the nudge target).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handlers.</param>
    /// <returns>The <c>_WatchlistControl</c> partial (HTMX) or a redirect (no-JS); <c>404</c> for bad input.</returns>
    [HttpDelete("")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Remove(
        int tmdbId, MediaType media, bool onDetails, CancellationToken cancellationToken)
    {
        if (tmdbId <= 0 || !Enum.IsDefined(media))
        {
            return NotFound();
        }

        var movieId = await sender.Send(new GetCachedMovieIdQuery(tmdbId, media), cancellationToken);
        if (movieId is { } id)
        {
            await sender.Send(new RemoveFromWatchlistCommand(id), cancellationToken);
        }

        // A not-yet-cached title was never in any list — nothing to remove, still the "Add" state.
        var control = new WatchlistControlVm(tmdbId, media, Status: null, IsAuthenticated: true)
        {
            OnDetails = onDetails,
        };

        if (IsHtmxRequest())
        {
            return PartialView("_WatchlistControl", control);
        }

        return RedirectToTitle(media, tmdbId);
    }

    /// <summary>
    /// The per-card lazy-hydrate FALLBACK (<c>GET /watchlist/control</c>): returns the <c>_WatchlistControl</c>
    /// fragment for one title with the current user's status. The batch status map is the primary path (§5.3);
    /// this endpoint exists for a surface that prefers per-card lazy hydration over the page-level map.
    /// </summary>
    /// <param name="tmdbId">The title's TMDB id.</param>
    /// <param name="media">The title's media type.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handlers.</param>
    /// <returns>The <c>_WatchlistControl</c> partial; <c>404</c> for bad input.</returns>
    [HttpGet("control")]
    public async Task<IActionResult> Control(int tmdbId, MediaType media, CancellationToken cancellationToken)
    {
        if (tmdbId <= 0 || !Enum.IsDefined(media))
        {
            return NotFound();
        }

        var movieId = await sender.Send(new GetCachedMovieIdQuery(tmdbId, media), cancellationToken);
        var status = movieId is { } id
            ? await sender.Send(new GetWatchlistStatusQuery(id), cancellationToken)
            : null;

        return PartialView("_WatchlistControl", new WatchlistControlVm(tmdbId, media, status, IsAuthenticated: true));
    }

    /// <summary>
    /// The watchlist page (<c>GET /watchlist?status=&amp;sort=</c>, Milestone 4.4 §6.2): the current user's own
    /// list, with per-status filter chips (counts from <see cref="GetWatchlistCountsQuery"/>), a sort control,
    /// and page 1 of <see cref="GetMyWatchlistQuery"/> rendered inline (a keyset page + a load-more sentinel).
    /// Owner-scoped server-side (ADR 0009) — no user id is bound. An unrecognised <paramref name="status"/> or
    /// <paramref name="sort"/> is treated as "All"/the default (never a 400/500).
    /// </summary>
    /// <param name="status">The status filter (<c>PlanToWatch</c>/<c>Watching</c>/<c>Watched</c>), or absent for "All".</param>
    /// <param name="sort">The sort order (index-backed; defaults to recently-added).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handlers.</param>
    /// <returns>The watchlist page view bound to page 1 + the per-status counts.</returns>
    [HttpGet("")]
    public async Task<IActionResult> Index(string? status, string? sort, CancellationToken cancellationToken)
    {
        var filter = ParseStatus(status);
        var sortOption = ParseSort(sort);

        var counts = await sender.Send(new GetWatchlistCountsQuery(), cancellationToken);
        var page = await sender.Send(
            new GetMyWatchlistQuery(filter, sortOption, Cursor: null), cancellationToken);

        return View(new WatchlistPageViewModel(page, counts, filter, sortOption));
    }

    /// <summary>
    /// The keyset load-more fragment (<c>GET /watchlist/page?status=&amp;sort=&amp;cursorKey=&amp;cursorId=</c>,
    /// §6.2): the next slice of watchlist cards plus a fresh sentinel (or a terminal marker), swapped in place of
    /// the previous sentinel. The opaque <c>(cursorKey, cursorId)</c> is the keyset cursor of the previous page's
    /// last item — never a page number/OFFSET. Owner-scoped server-side (ADR 0009).
    /// </summary>
    /// <param name="status">The status filter carried forward from the shell, or absent for "All".</param>
    /// <param name="sort">The sort order carried forward from the shell.</param>
    /// <param name="cursorKey">The keyset cursor sort key (the previous page's last <c>AddedAtUtc</c>).</param>
    /// <param name="cursorId">The keyset cursor tie-breaker (the previous page's last entry id).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handler.</param>
    /// <returns>The <c>_WatchlistPage</c> append fragment.</returns>
    [HttpGet("page")]
    public async Task<IActionResult> Page(
        string? status,
        string? sort,
        DateTime? cursorKey,
        Guid? cursorId,
        CancellationToken cancellationToken)
    {
        var filter = ParseStatus(status);
        var sortOption = ParseSort(sort);

        var cursor = cursorKey is { } key && cursorId is { } id
            ? new WatchlistCursor(key, id)
            : null;

        // Graceful degradation (mirrors HomeController.Feed): a post-query failure returns the 200 _WatchlistError
        // retry partial rather than a 500, because HTMX will not swap a non-2xx — a 500 would leave the sentinel
        // shimmering forever. Client aborts (OperationCanceledException) propagate unchanged.
        try
        {
            var page = await sender.Send(new GetMyWatchlistQuery(filter, sortOption, cursor), cancellationToken);
            return PartialView("_WatchlistPage", new WatchlistLoadMoreViewModel(page, filter, sortOption));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogWatchlistPageLoadFailed(logger, ex);
            var retryUrl = Url.Action("Page", "Watchlist", new
            {
                status = filter?.ToString(),
                sort = sortOption.ToString(),
                cursorKey = cursorKey?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                cursorId,
            }) ?? string.Empty;
            return PartialView("_WatchlistError", new WatchlistErrorVm(retryUrl));
        }
    }

    // Parses an optional status filter leniently: a defined WatchlistStatus name/number → that status; anything
    // else (absent, blank, unparseable, or an out-of-range number) → null ("All"), never a 400 (§6.2).
    private static WatchlistStatus? ParseStatus(string? value) =>
        Enum.TryParse<WatchlistStatus>(value, ignoreCase: true, out var status) && Enum.IsDefined(status)
            ? status
            : null;

    // Parses an optional sort leniently: a defined WatchlistSort → that sort; anything else → the default (§6.2).
    private static WatchlistSort ParseSort(string? value) =>
        Enum.TryParse<WatchlistSort>(value, ignoreCase: true, out var sort) && Enum.IsDefined(sort)
            ? sort
            : WatchlistSort.RecentlyAdded;

    // HTMX sets the "HX-Request" header on every AJAX request; its absence means a plain (no-JS) form post.
    private bool IsHtmxRequest() => Request.Headers.ContainsKey("HX-Request");

    private RedirectToActionResult RedirectToTitle(MediaType media, int tmdbId) =>
        RedirectToAction(
            "Details", "Discovery", new { media = media.ToString().ToLowerInvariant(), tmdbId });

    [LoggerMessage(
        EventId = 4400,
        Level = LogLevel.Warning,
        Message = "Loading a watchlist page failed; serving the retry partial (HTTP 200).")]
    private static partial void LogWatchlistPageLoadFailed(ILogger logger, Exception exception);
}
