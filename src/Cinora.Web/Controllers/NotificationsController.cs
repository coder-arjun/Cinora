using System.Globalization;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Notifications;
using Cinora.Web.Infrastructure;
using Cinora.Web.ViewModels.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

namespace Cinora.Web.Controllers;

/// <summary>
/// The authenticated notifications inbox (Milestone 3.5): the inbox shell + first keyset page, the lazy
/// load-more page, mark-one / mark-all read, and the no-JS unread-count badge fallback. The class is
/// <see cref="AuthorizeAttribute"/> (fail-closed) — the recipient is always the current user, resolved
/// server-side inside the handlers via <c>ICurrentUser</c>, so no recipient id is ever bound and there is no
/// cross-user read or mutation path. Writes carry the anti-forgery token via the already-wired
/// <c>RequestVerificationToken</c> header / hidden field (§10). The controller stays thin: it model-binds,
/// dispatches via <see cref="ISender"/>, and returns a view/partial/redirect — keyset paging, ownership, and the
/// unread count all live in the handlers.
/// </summary>
/// <param name="sender">The hand-rolled mediator used to dispatch the notification queries/commands.</param>
/// <param name="logger">Logs a Warning when a lazy inbox page read degrades to its retry partial (HTTP 200).</param>
[Authorize]
[Route("notifications")]
public sealed partial class NotificationsController(ISender sender, ILogger<NotificationsController> logger)
    : Controller
{
    /// <summary>The inbox page size — the number of notifications served per keyset page.</summary>
    private const int InboxPageSize = 20;

    /// <summary>
    /// The inbox shell (<c>GET /notifications</c>): the first keyset page of the current user's notifications,
    /// rendered inline with a load-more sentinel (or an empty state). A full-page read, so a failure surfaces
    /// through the global exception handler; the graceful-degradation path is the lazy <see cref="Feed"/> region.
    /// </summary>
    /// <param name="take">The page size (clamped in the handler); defaults to the inbox page size.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handler.</param>
    /// <returns>The inbox view bound to the first <see cref="NotificationListVm"/> page.</returns>
    [HttpGet("")]
    public async Task<IActionResult> Index(int take = InboxPageSize, CancellationToken cancellationToken = default)
    {
        var page = await sender.Send(new GetNotificationsQuery(Cursor: null, take), cancellationToken);
        return View(page);
    }

    /// <summary>
    /// The lazy load-more page (<c>GET /notifications/feed?cursorCreatedAt=&amp;cursorId=</c>): the next keyset
    /// slice of notification cards plus a fresh sentinel (or a terminal marker). Wrapped in the
    /// graceful-degradation try/catch so a post-query failure degrades to the 200 <c>_NotificationsError</c>
    /// retry partial (HTMX will not swap a non-2xx). Client aborts propagate.
    /// </summary>
    /// <param name="cursorCreatedAt">The keyset cursor timestamp (present only on load-more requests).</param>
    /// <param name="cursorId">The keyset cursor tie-breaker id (present only on load-more requests).</param>
    /// <param name="take">The page size (clamped in the handler).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handler.</param>
    /// <returns>The <c>_NotificationPage</c> append fragment, or the 200 <c>_NotificationsError</c> retry partial on failure.</returns>
    [HttpGet("feed")]
    public async Task<IActionResult> Feed(
        DateTime? cursorCreatedAt,
        Guid? cursorId,
        int take = InboxPageSize,
        CancellationToken cancellationToken = default)
    {
        var cursor = cursorCreatedAt is { } createdAt && cursorId is { } id
            ? new NotificationCursor(createdAt, id)
            : null;

        try
        {
            var page = await sender.Send(new GetNotificationsQuery(cursor, take), cancellationToken);
            return PartialView("_NotificationPage", page);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogInboxLoadFailed(logger, ex);
            var retryUrl = Url.Action("Feed", "Notifications", new
            {
                cursorCreatedAt = cursorCreatedAt?.ToString("o", CultureInfo.InvariantCulture),
                cursorId,
                take,
            }) ?? string.Empty;
            return PartialView("_NotificationsError", new NotificationsErrorVm(retryUrl));
        }
    }

    /// <summary>
    /// Marks one of the current user's notifications read (<c>POST /notifications/{id}/read</c>). A non-owned (or
    /// missing) id 404s inside <see cref="MarkNotificationReadCommand"/> (no cross-user mutation, N5). On success
    /// it redirects back to the inbox so the row re-renders in its read state (HTMX follows the redirect; the no-JS
    /// form post navigates) — the live single-row swap is the frontend's SignalR/HTMX enhancement.
    /// </summary>
    /// <param name="id">The notification id (from the route).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>A redirect to <see cref="Index"/>.</returns>
    [HttpPost("{id:guid}/read")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Read(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new MarkNotificationReadCommand(id), cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Marks ALL of the current user's unread notifications read (<c>POST /notifications/read-all</c>), then
    /// redirects back to the inbox (now with a zeroed badge).
    /// </summary>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>A redirect to <see cref="Index"/>.</returns>
    [HttpPost("read-all")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> ReadAll(CancellationToken cancellationToken)
    {
        await sender.Send(new MarkAllNotificationsReadCommand(), cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// The no-JS unread-count badge fallback (<c>GET /notifications/unread-count</c>): returns the badge partial
    /// for the current user's unread count. The frontend may poll this (or update the badge live over SignalR).
    /// </summary>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The <c>_UnreadBadge</c> partial bound to the unread count.</returns>
    [HttpGet("unread-count")]
    public async Task<IActionResult> UnreadCount(CancellationToken cancellationToken)
    {
        var count = await sender.Send(new GetUnreadCountQuery(), cancellationToken);
        return PartialView("_UnreadBadge", count);
    }

    [LoggerMessage(
        EventId = 3501,
        Level = LogLevel.Warning,
        Message = "Loading a notifications inbox page failed; serving the retry partial (HTTP 200).")]
    private static partial void LogInboxLoadFailed(ILogger logger, Exception exception);
}
