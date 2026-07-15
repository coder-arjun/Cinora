using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Feed;
using Cinora.Web.ViewModels.Feed;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cinora.Web.Controllers;

/// <summary>
/// Serves the public landing page and the authenticated home shell. The landing page is reachable
/// anonymously; the home shell requires an authenticated session, and when one is absent the Identity
/// application cookie challenges to <c>/account/login</c>. As of Milestone 3.4 the home shell is the friends
/// activity feed (ADR 0012): <c>GET /home</c> renders the first keyset page inline (plus a self-replacing
/// load-more sentinel), and <c>GET /home/feed</c> serves each subsequent page as an HTMX partial. The
/// controller stays thin: it model-binds, dispatches via <see cref="ISender"/>, and returns a view/partial —
/// the friend-set resolution, keyset paging, and projection all live in <c>GetActivityFeedQuery</c>.
/// </summary>
/// <param name="sender">The hand-rolled mediator used to dispatch the activity-feed query.</param>
/// <param name="logger">Logs a Warning when a lazy feed page read degrades to its retry partial (HTTP 200).</param>
[Authorize]
public sealed partial class HomeController(ISender sender, ILogger<HomeController> logger) : Controller
{
    /// <summary>The feed page size — the number of items served per keyset page.</summary>
    private const int FeedPageSize = 20;

    /// <summary>
    /// The public marketing landing page (<c>GET /</c>). Signed-in users never see it — the "Get started /
    /// Sign in" marketing surface is for logged-out visitors only — so an authenticated request is redirected to
    /// the language-based Discover browse instead.
    /// </summary>
    /// <returns>The landing view for anonymous visitors, or a redirect to Discover when already signed in.</returns>
    [AllowAnonymous]
    [HttpGet("/")]
    public IActionResult Landing() =>
        User.Identity?.IsAuthenticated == true
            ? RedirectToAction("Index", "Discovery")
            : View();

    /// <summary>
    /// The authenticated home shell (<c>GET /home</c>): the first keyset page of the current user's friends
    /// feed, rendered inline with a load-more sentinel (or one of the two empty states). This is a full page
    /// read (like <c>/friends</c> and <c>/users/{id}</c>), so a read failure surfaces through the global
    /// exception handler rather than the lazy retry partial — the graceful-degradation path is the lazy
    /// <see cref="Feed"/> region below.
    /// </summary>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handler.</param>
    /// <returns>The home feed view bound to the first <see cref="ActivityFeedVm"/> page.</returns>
    [HttpGet("/home")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var feed = await sender.Send(new GetActivityFeedQuery(Cursor: null, FeedPageSize), cancellationToken);
        return View(feed);
    }

    /// <summary>
    /// The lazy load-more page (<c>GET /home/feed?cursorCreatedAt=&amp;cursorId=</c>): the next keyset slice of
    /// feed cards plus a fresh sentinel (or a terminal marker). Wrapped in the graceful-degradation try/catch so
    /// a post-query failure degrades to the 200 <c>_FeedError</c> retry partial (HTMX will not swap a non-2xx,
    /// so a 500 would leave the region shimmering forever — the 2.3 lesson). Client aborts propagate.
    /// </summary>
    /// <param name="cursorCreatedAt">The keyset cursor timestamp (present only on load-more requests).</param>
    /// <param name="cursorId">The keyset cursor tie-breaker id (present only on load-more requests).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handler.</param>
    /// <returns>The <c>_FeedPage</c> append fragment, or the 200 <c>_FeedError</c> retry partial on failure.</returns>
    [HttpGet("/home/feed")]
    public async Task<IActionResult> Feed(
        DateTime? cursorCreatedAt, Guid? cursorId, CancellationToken cancellationToken)
    {
        var cursor = cursorCreatedAt is { } createdAt && cursorId is { } id
            ? new FeedCursor(createdAt, id)
            : null;

        try
        {
            var feed = await sender.Send(new GetActivityFeedQuery(cursor, FeedPageSize), cancellationToken);
            return PartialView("_FeedPage", feed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFeedLoadFailed(logger, ex);
            var retryUrl = Url.Action("Feed", "Home", new
            {
                cursorCreatedAt = cursorCreatedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                cursorId,
            }) ?? string.Empty;
            return PartialView("_FeedError", new FeedErrorVm(retryUrl));
        }
    }

    [LoggerMessage(
        EventId = 3400,
        Level = LogLevel.Warning,
        Message = "Loading an activity feed page failed; serving the retry partial (HTTP 200).")]
    private static partial void LogFeedLoadFailed(ILogger logger, Exception exception);
}
