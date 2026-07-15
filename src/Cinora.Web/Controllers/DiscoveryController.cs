using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Catalog;
using Cinora.Application.Features.Discovery;
using Cinora.Application.Features.Reviews;
using Cinora.Application.Features.Watchlist;
using Cinora.Domain.Enums;
using Cinora.Web.Infrastructure;
using Cinora.Web.ViewModels.Discovery;
using Cinora.Web.ViewModels.Reviews;
using Cinora.Web.ViewModels.Watchlist;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

namespace Cinora.Web.Controllers;

/// <summary>
/// The title-discovery surface (Milestone 2.3). <c>GET /discover</c> renders a static three-rail Home shell;
/// <c>GET /discover/rail</c> returns one rail's cards as an HTMX partial. Search (2.4) and Details (2.5) nest
/// under the same <c>/discover</c> space.
/// </summary>
/// <remarks>
/// The whole controller now REQUIRES authentication (ADR 0023 — Cinora is login-only): it carries no
/// <c>[AllowAnonymous]</c> opt-out, so it inherits the global fail-closed fallback policy and an anonymous
/// request is redirected to login. Every action is GET-only, so it is exempt from the global anti-forgery
/// validation (which only guards unsafe verbs). Controllers stay thin: model-bind, dispatch via
/// <see cref="ISender"/>, return a view/partial — no business logic here.
/// </remarks>
/// <param name="sender">The hand-rolled mediator used to dispatch the rail query.</param>
/// <param name="logger">Logs a Warning when a non-critical rail degrades to its error/retry partial.</param>
[EnableRateLimiting(RateLimitingPolicies.PublicRead)]
[Route("discover")]
public sealed partial class DiscoveryController(ISender sender, ILogger<DiscoveryController> logger) : Controller
{
    /// <summary>
    /// The search-as-you-type UX threshold: below this length the presentation layer treats the box as idle
    /// (the <see cref="SearchResults"/> action returns a 200 prompt WITHOUT dispatching, and the Alpine
    /// component does not fire). Kept in sync with the Alpine component's <c>MIN_LENGTH</c>. This is a
    /// presentation threshold layered ON TOP of the validator's contract floor of 1 (<c>NotEmpty</c>) — it is
    /// not a contract change.
    /// </summary>
    private const int MinQueryLength = 2;

    /// <summary>
    /// The Home / discovery shell: three skeleton rails, each lazy-loaded by the view via HTMX. Resolves the
    /// effective original-language filter — an explicit <c>?language=</c> (even empty = Global) wins; when the
    /// param is ABSENT, a signed-in user's saved default applies; anonymous falls back to Global. Only the
    /// default lookup touches <see cref="ISender"/> (authed path); the rails themselves stay lazy.
    /// </summary>
    /// <param name="language">The chosen language code; absent (<c>null</c>) means "use my default / Global".</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handler.</param>
    /// <returns>The discovery shell view bound to the three-Movie-rail config for the effective language.</returns>
    [HttpGet("")]
    public async Task<IActionResult> Index(string? language, CancellationToken cancellationToken)
    {
        string? effective;
        if (language is null)
        {
            // No explicit choice: use the signed-in user's saved default (Global for anonymous).
            effective = IsAuthenticated
                ? await sender.Send(new GetMyDefaultLanguageQuery(), cancellationToken)
                : null;
        }
        else
        {
            // Explicit choice (including "" = Global): validate/normalize against the curated catalog.
            effective = LanguageOptions.Normalize(language);
        }

        return View(HomeFeedViewModel.ForLanguage(effective));
    }

    /// <summary>
    /// Returns one rail's cards as the <c>_Rail</c> partial (no layout) for the shell's per-rail HTMX
    /// lazy-load.
    /// </summary>
    /// <param name="kind">Which curated rail to render (Trending / Popular / Top Rated).</param>
    /// <param name="media">The media type (Movie or Series) to fetch.</param>
    /// <param name="language">The original-language filter (validated); absent/blank/unsupported = Global.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handler.</param>
    /// <returns>The <c>_Rail</c> partial with the cards, or <c>400</c> when <paramref name="kind"/> or
    /// <paramref name="media"/> is not a valid, defined enum value.</returns>
    [HttpGet("rail")]
    public async Task<IActionResult> Rail(RailKind kind, MediaType media, string? language, CancellationToken cancellationToken)
    {
        // Two complementary guards turn any bogus enum into a real 400 rather than a silently-served default
        // rail. This controller is deliberately NOT [ApiController] (it returns views), so it does not
        // auto-400 on a bad enum and must guard explicitly:
        //   • !ModelState.IsValid catches an UNPARSEABLE string (e.g. ?kind=Bogus): the binder rejects it and
        //     leaves the parameter at its default (Trending), which Enum.IsDefined alone would accept.
        //   • !Enum.IsDefined catches an OUT-OF-RANGE numeric value (e.g. ?kind=99) that binds "successfully"
        //     to an undefined enum member.
        if (!ModelState.IsValid || !Enum.IsDefined(kind) || !Enum.IsDefined(media))
        {
            return BadRequest();
        }

        try
        {
            // Validate the language against the curated catalog (unsupported/blank → Global) before threading it.
            var rail = await sender.Send(new GetTitleRailQuery(kind, media, LanguageOptions.Normalize(language)), cancellationToken);
            // 4.3 (ADR 0014): compose the per-user watchlist statuses (one batch query, authed only) so each
            // card renders its _WatchlistControl without an N+1. The rail query itself stays ITmdbClient-only.
            var statuses = await ResolveWatchlistStatusesAsync(rail.Items, media, cancellationToken);
            return PartialView("_Rail", new RailViewModel(rail, statuses, IsAuthenticated));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A rail is a NON-CRITICAL, independently lazy-loaded partial. If its TMDB read fails AFTER the
            // standard resilience pipeline has already retried (an HttpRequestException from a failed status,
            // or a Polly BrokenCircuitException / TimeoutRejectedException it surfaces), letting it bubble to
            // the GlobalExceptionHandler yields a 500 — and HTMX does NOT swap a non-2xx response, so the
            // skeleton would shimmer forever with aria-busy="true" and never recover. Degrade gracefully:
            // log a Warning and return the on-brand _RailError retry partial with HTTP 200 so HTMX swaps it
            // and the user can retry just this row. A scoped Exception catch (excluding cancellation) is a
            // deliberate choice for graceful degradation of one optional row — we log exactly what we caught.
            // Client aborts (OperationCanceledException) are left to propagate: no one is waiting for a retry.
            // NOTE: the Enum.IsDefined 400 above is untouched — bad client input stays a 400, not this partial.
            LogRailLoadFailed(logger, ex, kind, media);
            return PartialView("_RailError", new RailErrorViewModel(kind, media));
        }
    }

    /// <summary>
    /// The full Search page (<c>GET /discover/search</c>): the debounced input plus a results region. When
    /// <paramref name="q"/> is present and at least <see cref="MinQueryLength"/> characters, page 1 is
    /// server-rendered into the region (deep-link + no-JS baseline); otherwise the idle prompt is shown.
    /// </summary>
    /// <param name="q">The free-text query; trimmed, and guarded to the prompt when too short.</param>
    /// <param name="media">The media type to search (defaults to Movie).</param>
    /// <param name="page">The 1-based result page (clamped to ≥ 1).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handler.</param>
    /// <returns>The Search view, or <c>400</c> when <paramref name="media"/> is not a valid enum value.</returns>
    [HttpGet("search")]
    public async Task<IActionResult> Search(
        string? q,
        MediaType media = MediaType.Movie,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        // Same guard grammar as Rail (§10.5): a non-[ApiController] controller does not auto-400 a bad enum.
        // !ModelState.IsValid catches an unparseable ?media=Bogus (binder leaves the default + flags state);
        // !Enum.IsDefined catches an out-of-range numeric value that bound to an undefined member. page/q need
        // no 400 guard: page is clamped to ≥ 1 below and q is guarded to the prompt.
        if (!ModelState.IsValid || !Enum.IsDefined(media))
        {
            return BadRequest();
        }

        var term = q?.Trim();
        SearchResultsVm? results = null;
        IReadOnlyDictionary<int, WatchlistStatus> statuses = EmptyStatuses;
        if (term is { Length: >= MinQueryLength })
        {
            results = await sender.Send(new SearchTitlesQuery(term, media, Math.Max(1, page)), cancellationToken);
            statuses = await ResolveWatchlistStatusesAsync(results.Items, media, cancellationToken);
        }

        return View(new SearchPageViewModel(term ?? string.Empty, media, results, statuses, IsAuthenticated));
    }

    /// <summary>
    /// The HTMX results partial (<c>GET /discover/search/results</c>): the Alpine-fired live search and the
    /// load-more sentinel target this. Returns <c>_SearchResults</c> (page 1) / <c>_SearchResultsPage</c>
    /// (page &gt; 1) for a valid query, the 200 <c>_SearchPrompt</c> for a too-short one, or the 200
    /// <c>_SearchError</c> retry partial on a post-resilience TMDB failure.
    /// </summary>
    /// <param name="q">The free-text query; trimmed, and guarded to the prompt when too short.</param>
    /// <param name="media">The media type to search (defaults to Movie).</param>
    /// <param name="page">The 1-based result page (clamped to ≥ 1); only the sentinel supplies page &gt; 1.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handler.</param>
    /// <returns>A results / page / prompt / error partial (always HTTP 200 for the browser paths), or
    /// <c>400</c> when <paramref name="media"/> is not a valid enum value.</returns>
    [HttpGet("search/results")]
    public async Task<IActionResult> SearchResults(
        string? q,
        MediaType media = MediaType.Movie,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid || !Enum.IsDefined(media))
        {
            return BadRequest();
        }

        var term = q?.Trim();
        if (term is not { Length: >= MinQueryLength })
        {
            // An empty/too-short box is the IDLE state, not an error: return the prompt with HTTP 200 and
            // NEVER dispatch — so HTMX always receives a 2xx to swap (a non-2xx would leave the region
            // spinning, the 2.3 lesson). The validator's 400 stays reachable only by a DIRECT dispatch (test).
            return PartialView("_SearchPrompt", new SearchPromptViewModel(media));
        }

        try
        {
            var results = await sender.Send(new SearchTitlesQuery(term, media, Math.Max(1, page)), cancellationToken);
            // 4.3 (ADR 0014): compose the per-user statuses (one batch query, authed only) into the view model
            // so each card renders its _WatchlistControl without an N+1. The search query stays ITmdbClient-only.
            var statuses = await ResolveWatchlistStatusesAsync(results.Items, media, cancellationToken);
            var model = new SearchResultsViewModel(results, statuses, IsAuthenticated);
            // page 1 = the full results region (summary + cards + sentinel); page > 1 = the append fragment.
            return PartialView(page > 1 ? "_SearchResultsPage" : "_SearchResults", model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 2.3 grammar: a post-resilience TMDB failure must NOT become a 500 (HTMX won't swap a non-2xx).
            // Log a Warning and return the on-brand _SearchError retry partial with HTTP 200 so HTMX swaps it
            // and the user can retry. Client aborts (OperationCanceledException) are left to propagate.
            LogSearchFailed(logger, ex, media, page);
            // Echo the CLAMPED page (≥ 1) into the retry — the same value the dispatch used — so a crafted
            // ?page=0 failure can't put page=0 into the Retry URL.
            return PartialView("_SearchError", new SearchErrorViewModel(term, media, Math.Max(1, page)));
        }
    }

    /// <summary>
    /// The title Details page (<c>GET /discover/title/{media}/{tmdbId}</c>): a backdrop hero, poster,
    /// metadata, genres, top-billed cast, a community-rating placeholder (Cinora reviews arrive Phase 3) and
    /// an add-to-watchlist stub (wired in Phase 4). After the display read it dispatches the idempotent
    /// <see cref="EnsureTitleCachedCommand"/> to first-touch persist the internal <c>Movie</c> row.
    /// </summary>
    /// <param name="media">The media segment (the lowercase enum name the cards link with, e.g. <c>movie</c>).</param>
    /// <param name="tmdbId">The TMDB identifier of the title.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handlers.</param>
    /// <returns>The Details view, or <c>404</c> when the media segment is unknown or TMDB has no such title.</returns>
    [HttpGet("title/{media}/{tmdbId:int}")]
    public async Task<IActionResult> Details(string media, int tmdbId, CancellationToken cancellationToken)
    {
        // The cards link with the lowercase enum name (e.g. /discover/title/movie/550). Parse it back; an
        // unknown media segment or a non-positive id is a non-existent title page → 404 (not a 400: the
        // segment is part of the resource path, so an unmatched value means "no such page").
        if (tmdbId <= 0
            || !Enum.TryParse<MediaType>(media, ignoreCase: true, out var mediaType)
            || !Enum.IsDefined(mediaType))
        {
            return NotFound();
        }

        var details = await sender.Send(new GetTitleDetailsQuery(mediaType, tmdbId), cancellationToken);
        if (details is null)
        {
            return NotFound(); // TMDB has no such title (a 404).
        }

        // First-touch persistence (ADR 0007): dispatch the idempotent EnsureTitleCachedCommand AFTER the read
        // so the internal Movie row exists for Phase 3–4 (reviews/watchlist). The read just warmed the cache,
        // so this is a cache hit — no extra TMDB call. It ALSO yields the internal MovieId used to resolve the
        // current user's watchlist status (§5.3, 4.3). It is a SIDE-BENEFIT, not required to display the page:
        // a persistence failure must not break a public browse page, so degrade gracefully — log a Warning and
        // still render (the control then shows the "Add" state). Client aborts are left to propagate.
        WatchlistStatus? watchlistStatus = null;
        try
        {
            var ensure = await sender.Send(new EnsureTitleCachedCommand(tmdbId, mediaType), cancellationToken);
            if (IsAuthenticated && ensure.MovieId is { } movieId)
            {
                watchlistStatus = await sender.Send(new GetWatchlistStatusQuery(movieId), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogTitlePersistFailed(logger, ex, mediaType, tmdbId);
        }

        // The inline watchlist control (§5.3): OnDetails drives the Watched → #my-review review nudge (§5.4).
        ViewData["WatchlistControl"] =
            new WatchlistControlVm(tmdbId, mediaType, watchlistStatus, IsAuthenticated) { OnDetails = true };

        return View(details);
    }

    /// <summary>
    /// The public, keyset-paginated reviews list for a title (<c>GET /discover/title/{media}/{tmdbId}/reviews</c>).
    /// Stays anonymous (inherited class-level <see cref="AllowAnonymousAttribute"/>) and is lazy-loaded into the
    /// Details page plus advanced by a self-replacing load-more sentinel. The public route addresses the title by
    /// its TMDB coordinates; this resolves them to the internal <c>MovieId</c> via <see cref="GetCachedMovieIdQuery"/>
    /// (a title not yet first-touch persisted simply has no reviews → an empty list, never an error).
    /// </summary>
    /// <param name="media">The media segment (lowercase enum name, e.g. <c>movie</c>).</param>
    /// <param name="tmdbId">The TMDB identifier of the title.</param>
    /// <param name="cursorCreatedAt">The keyset cursor timestamp (present only on load-more requests).</param>
    /// <param name="cursorId">The keyset cursor tie-breaker id (present only on load-more requests).</param>
    /// <param name="take">The page size (clamped in the handler).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handlers.</param>
    /// <returns>The <c>_ReviewList</c> partial (first page) or <c>_ReviewListPage</c> append fragment (a cursor
    /// request); <c>404</c> when the media segment is unknown or the id is non-positive.</returns>
    [HttpGet("title/{media}/{tmdbId:int}/reviews")]
    public async Task<IActionResult> Reviews(
        string media,
        int tmdbId,
        DateTime? cursorCreatedAt,
        Guid? cursorId,
        int take = 10,
        CancellationToken cancellationToken = default)
    {
        if (tmdbId <= 0
            || !Enum.TryParse<MediaType>(media, ignoreCase: true, out var mediaType)
            || !Enum.IsDefined(mediaType))
        {
            return NotFound();
        }

        ReviewCursor? cursor = cursorCreatedAt is { } createdAt && cursorId is { } id
            ? new ReviewCursor(createdAt, id)
            : null;

        try
        {
            var movieId = await sender.Send(new GetCachedMovieIdQuery(tmdbId, mediaType), cancellationToken);

            // A not-yet-cached title has no local Movie row and therefore no reviews — a calm empty page.
            var reviews = movieId is { } resolvedMovieId
                ? await sender.Send(new GetTitleReviewsQuery(resolvedMovieId, cursor, take), cancellationToken)
                : new TitleReviewsVm([], NextCursor: null, HasMore: false, MovieId: Guid.Empty);

            var model = new ReviewListVm(reviews, mediaType, tmdbId);

            // First page (no cursor) renders the whole list region; a cursor request returns the append fragment.
            return PartialView(cursor is null ? "_ReviewList" : "_ReviewListPage", model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The reviews list is a lazy region: a post-read failure must degrade to the 200 retry partial, not
            // a 500 (HTMX won't swap a non-2xx, so it would shimmer forever — the 2.3 lesson). Log a Warning;
            // the Retry re-fires this exact page (params echoed). Client aborts are left to propagate.
            LogReviewsLoadFailed(logger, ex, mediaType, tmdbId);
            var retryUrl = Url.Action("Reviews", "Discovery", new
            {
                media,
                tmdbId,
                cursorCreatedAt = cursorCreatedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                cursorId,
                take,
            }) ?? string.Empty;
            return PartialView("_ReviewsError", new ReviewsErrorVm(retryUrl));
        }
    }

    // The map handed to a rail/search view model for an anonymous browse (no per-user status, no DB touch).
    private static readonly IReadOnlyDictionary<int, WatchlistStatus> EmptyStatuses =
        new Dictionary<int, WatchlistStatus>();

    // Whether the current browse request is authenticated (drives the batch map + the card watchlist control).
    private bool IsAuthenticated => User.Identity?.IsAuthenticated == true;

    // Resolves the current user's watchlist status for a page of cards in ONE batch query (ADR 0014).
    // Anonymous browse (or an empty page) skips the query entirely, so the rail/search partial stays pure and
    // cacheable and cards render the sign-in affordance; authed browse gets a status per in-list title.
    private async Task<IReadOnlyDictionary<int, WatchlistStatus>> ResolveWatchlistStatusesAsync(
        IReadOnlyList<TitleCardVm> items, MediaType media, CancellationToken cancellationToken)
    {
        if (!IsAuthenticated || items.Count == 0)
        {
            return EmptyStatuses;
        }

        var tmdbIds = items.Select(item => item.TmdbId).ToArray();
        return await sender.Send(new GetWatchlistStatusMapQuery(tmdbIds, media), cancellationToken);
    }

    [LoggerMessage(
        EventId = 2600,
        Level = LogLevel.Warning,
        Message = "Loading reviews for {Media} TMDB id {TmdbId} failed; serving the retry partial (HTTP 200).")]
    private static partial void LogReviewsLoadFailed(ILogger logger, Exception exception, MediaType media, int tmdbId);

    [LoggerMessage(
        EventId = 2300,
        Level = LogLevel.Warning,
        Message = "Discovery rail load failed for {Kind}/{Media}; serving the error/retry partial (HTTP 200).")]
    private static partial void LogRailLoadFailed(ILogger logger, Exception exception, RailKind kind, MediaType media);

    [LoggerMessage(
        EventId = 2400,
        Level = LogLevel.Warning,
        Message = "Discovery search failed for media {Media} page {Page}; serving the error/retry partial (HTTP 200).")]
    private static partial void LogSearchFailed(ILogger logger, Exception exception, MediaType media, int page);

    [LoggerMessage(
        EventId = 2500,
        Level = LogLevel.Warning,
        Message = "First-touch catalog persistence failed for {Media} TMDB id {TmdbId}; the Details page still rendered.")]
    private static partial void LogTitlePersistFailed(ILogger logger, Exception exception, MediaType media, int tmdbId);
}
