using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Recommendations;
using Cinora.Domain.Enums;
using Cinora.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

namespace Cinora.Web.Controllers;

/// <summary>
/// The authenticated "For You" recommendation surface (Milestone 5.4, Phase 5 design §11): the full
/// <c>GET /recommendations</c> page, the <c>GET /recommendations/rail</c> partial the <c>/home</c> feed lazy-loads,
/// and the <c>POST /recommendations/{tmdbId}/dismiss</c> feedback control. The class is
/// <see cref="AuthorizeAttribute"/> (fail-closed) and the served set is owner-implicit: EVERY read dispatches only
/// <see cref="GetMyRecommendationsQuery"/>, which resolves the acting user server-side from <c>ICurrentUser</c>
/// (ADR 0009 — no id is bound) and NEVER calls the LLM (cache → history → heuristic, design §7/§15). No action
/// references <c>IRecommendationEngine</c> — the headline exit criterion "page load makes no LLM call."
/// </summary>
/// <remarks>
/// Reads carry no rate-limit attribute (they are cheap cache/DB reads, mirroring <c>WatchlistController.Index</c>);
/// only the one state-changing verb (dismiss) is rate-limited and anti-forgery-validated. The browser never
/// contacts the model, so no CSP directive widens (design §13): the rail/page are same-origin HTMX over
/// TMDB posters already allowed since Phase 2.
/// </remarks>
/// <param name="sender">The hand-rolled mediator used to dispatch the LLM-free serve query.</param>
/// <param name="logger">Logs a Warning when the non-critical For-You rail degrades to its quiet 200 fallback.</param>
[Authorize]
[Route("recommendations")]
public sealed partial class RecommendationsController(ISender sender, ILogger<RecommendationsController> logger)
    : Controller
{
    /// <summary>
    /// The full "For You" page (<c>GET /recommendations</c>): the current user's served <see cref="RecommendationSet"/>
    /// rendered as a responsive poster grid. Dispatches <see cref="GetMyRecommendationsQuery"/> only (no LLM). The
    /// view labels the set honestly by <c>Source</c> (AI → "For You", heuristic → "Popular picks") and shows a calm
    /// empty state when there are no picks yet.
    /// </summary>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handler.</param>
    /// <returns>The For-You page bound to the served set.</returns>
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var recommendations = await sender.Send(new GetMyRecommendationsQuery(), cancellationToken);
        return View(recommendations);
    }

    /// <summary>
    /// The For-You rail partial (<c>GET /recommendations/rail</c>) the authenticated <c>/home</c> feed lazy-loads
    /// (HTMX <c>hx-trigger="load"</c>). Same LLM-free query as the page. A serve failure degrades to the quiet
    /// <c>_RecommendationsRailError</c> partial with HTTP 200 — HTMX will not swap a non-2xx, so a 500 here would
    /// leave the skeleton shimmering forever; the rail is a bonus above the feed and must never surface an error.
    /// </summary>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handler.</param>
    /// <returns>The <c>_RecommendationsRail</c> partial, or the quiet <c>_RecommendationsRailError</c> fallback (200).</returns>
    [HttpGet("rail")]
    public async Task<IActionResult> Rail(CancellationToken cancellationToken)
    {
        try
        {
            var recommendations = await sender.Send(new GetMyRecommendationsQuery(), cancellationToken);
            return PartialView("_RecommendationsRail", recommendations);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The serve query already degrades internally (a cache fault falls through to history/heuristic, and the
            // heuristic swallows TMDB faults), so reaching here is unlikely — but the rail is non-critical, so a
            // stray failure returns the calm 200 fallback rather than bubbling to a 500 the skeleton can't recover
            // from. Client aborts (OperationCanceledException) are left to propagate.
            LogRailLoadFailed(logger, ex);
            return PartialView("_RecommendationsRailError");
        }
    }

    /// <summary>
    /// Dismisses a recommended title (<c>POST /recommendations/{tmdbId}/dismiss</c>): the card is removed on the
    /// client (an empty 200 body → <c>hx-swap="outerHTML"</c> drops the card's <c>&lt;li&gt;</c>). The write is
    /// fail-closed (<see cref="AuthorizeAttribute"/>), per-user rate-limited, and validated by the global
    /// <c>AutoValidateAntiforgeryToken</c> (the token rides the <c>RequestVerificationToken</c> header site.ts
    /// attaches — no per-region CSRF work).
    /// <para>
    /// <b>v1 persists NOTHING.</b> Durable "not interested" is DEFERRED (ADR 0018 §11 / design §11): there is no
    /// per-title recommendation-feedback field in the schema, and the candidate pipeline already excludes every
    /// title the user has reviewed or watchlisted (design §5.1), so acting on a pick drops it from the next nightly
    /// refresh. This endpoint is the anti-forgery-protected, rate-limited seam a durable <c>RecommendationFeedback</c>
    /// store slots into later WITHOUT reshaping the UI — the client contract (empty 200 → card removed) stays.
    /// </para>
    /// </summary>
    /// <param name="tmdbId">The dismissed title's TMDB id (route-bound; must be positive).</param>
    /// <param name="media">The dismissed title's media type (form-bound via <c>hx-vals</c>; must be a defined enum).</param>
    /// <returns>An empty <c>200</c> so HTMX removes the card, or <c>404</c> for bad input.</returns>
    [HttpPost("{tmdbId:int}/dismiss")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public IActionResult Dismiss(int tmdbId, [FromForm] MediaType media)
    {
        if (tmdbId <= 0 || !Enum.IsDefined(media))
        {
            return NotFound();
        }

        // Empty 200: the card's hx-swap="outerHTML" replaces its <li> with nothing, removing it. No persistence
        // (see the deferred-feedback note above). Kept intentionally trivial so the durable store is a pure add.
        return Content(string.Empty);
    }

    [LoggerMessage(
        EventId = 5400,
        Level = LogLevel.Warning,
        Message = "Loading the For-You recommendation rail failed; serving the quiet fallback partial (HTTP 200).")]
    private static partial void LogRailLoadFailed(ILogger logger, Exception exception);
}
