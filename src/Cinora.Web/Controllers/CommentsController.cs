using System.Globalization;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Comments;
using Cinora.Web.Infrastructure;
using Cinora.Web.ViewModels.Reviews;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

namespace Cinora.Web.Controllers;

/// <summary>
/// The comments surface for reviews (Milestone 3.2): add and delete a comment (authenticated writes) plus the
/// PUBLIC comment thread (an <see cref="AllowAnonymousAttribute"/> read). The class is <see cref="AuthorizeAttribute"/>
/// (fail-closed) and the public list opts out per-action. The controller stays thin: it model-binds, dispatches
/// via <see cref="ISender"/>, and returns an HTMX partial — ownership and business rules live in the handlers;
/// the acting user is server-resolved via <c>ICurrentUser</c>, so no author id is ever bound (ADR 0009).
/// </summary>
/// <param name="sender">The hand-rolled mediator used to dispatch the comment commands/queries.</param>
/// <param name="logger">Logs a Warning when the lazy comment read degrades to its retry partial (HTTP 200).</param>
[Authorize]
public sealed partial class CommentsController(ISender sender, ILogger<CommentsController> logger) : Controller
{
    /// <summary>
    /// Adds a comment to a review (<c>POST /reviews/{id}/comments</c>) and returns the new <c>_CommentCard</c>
    /// to append. A missing review 404s inside <see cref="AddCommentCommand"/>.
    /// </summary>
    /// <param name="id">The review id (from the route).</param>
    /// <param name="form">The bound comment form (body).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The <c>_CommentCard</c> partial for the created comment.</returns>
    [HttpPost("reviews/{id:guid}/comments")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Add(Guid id, [FromForm] CommentForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        var result = await sender.Send(new AddCommentCommand(id, form.Body), cancellationToken);
        return PartialView("_CommentCard", result.Card);
    }

    /// <summary>
    /// Deletes the current user's comment (<c>DELETE /comments/{id}</c>) and returns an empty <c>200</c> so HTMX
    /// removes the node. Ownership is enforced (403) and a missing comment 404s — inside
    /// <see cref="DeleteCommentCommand"/>.
    /// </summary>
    /// <param name="id">The comment id.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>An empty <c>200</c>.</returns>
    [HttpDelete("comments/{id:guid}")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteCommentCommand(id), cancellationToken);
        return Ok();
    }

    /// <summary>
    /// The keyset-paginated comment thread for a review (<c>GET /reviews/{id}/comments</c>), oldest-first.
    /// Authenticated (ADR 0023 — Cinora is login-only; no anonymous opt-out); a post-read failure degrades to the
    /// 200 retry partial rather than a 500 (HTMX will not swap a non-2xx). Reuses the region-scoped error grammar.
    /// </summary>
    /// <param name="id">The review id.</param>
    /// <param name="cursorCreatedAt">The keyset cursor timestamp (present only on load-more requests).</param>
    /// <param name="cursorId">The keyset cursor tie-breaker id (present only on load-more requests).</param>
    /// <param name="take">The page size (clamped in the handler).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The <c>_CommentList</c> partial (first page) or <c>_CommentListPage</c> append fragment.</returns>
    [HttpGet("reviews/{id:guid}/comments")]
    public async Task<IActionResult> List(
        Guid id,
        DateTime? cursorCreatedAt,
        Guid? cursorId,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        CommentCursor? cursor = cursorCreatedAt is { } createdAt && cursorId is { } cursorGuid
            ? new CommentCursor(createdAt, cursorGuid)
            : null;

        try
        {
            var comments = await sender.Send(new GetReviewCommentsQuery(id, cursor, take), cancellationToken);

            // First page (no cursor) renders the whole thread region; a cursor request returns the append fragment.
            return PartialView(cursor is null ? "_CommentList" : "_CommentListPage", comments);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A lazy region must NOT surface a 500 (HTMX won't swap a non-2xx). Log a Warning; the Retry
            // re-fires this exact page. Client aborts are left to propagate.
            LogCommentsLoadFailed(logger, ex, id);
            var retryUrl = Url.Action("List", "Comments", new
            {
                id,
                cursorCreatedAt = cursorCreatedAt?.ToString("o", CultureInfo.InvariantCulture),
                cursorId,
                take,
            }) ?? string.Empty;
            return PartialView("_ReviewsError", new ReviewsErrorVm(retryUrl));
        }
    }

    [LoggerMessage(
        EventId = 3200,
        Level = LogLevel.Warning,
        Message = "Loading comments for review {ReviewId} failed; serving the retry partial (HTTP 200).")]
    private static partial void LogCommentsLoadFailed(ILogger logger, Exception exception, Guid reviewId);
}
