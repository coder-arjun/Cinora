using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Catalog;
using Cinora.Application.Features.Reviews;
using Cinora.Domain.Enums;
using Cinora.Web.Infrastructure;
using Cinora.Web.ViewModels.Reviews;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

namespace Cinora.Web.Controllers;

/// <summary>
/// The authenticated write surface for Cinora reviews (Milestone 3.1): create, edit, and delete a review, plus
/// the per-user "your review" fragment on the Details page. Every action requires authentication (the class is
/// <see cref="AuthorizeAttribute"/>; the global anti-forgery filter validates the token every unsafe verb
/// carries via the <c>RequestVerificationToken</c> header / hidden field — §5.3). The controller stays thin:
/// it model-binds, dispatches via <see cref="ISender"/>, and returns an HTMX partial or a redirect — ownership
/// and all business rules live in the handlers (ADR 0009). The acting user is server-resolved inside those
/// handlers via <c>ICurrentUser</c>, so no author id is ever bound from the request.
/// </summary>
/// <param name="sender">The hand-rolled mediator used to dispatch the review commands/queries.</param>
/// <param name="logger">Logs a Warning when a lazy review read degrades to its retry partial (HTTP 200).</param>
[Authorize]
[Route("reviews")]
public sealed partial class ReviewsController(ISender sender, ILogger<ReviewsController> logger) : Controller
{
    /// <summary>
    /// Creates the current user's review of a title (<c>POST /reviews</c>). It orchestrates the ADR-0008 seam
    /// (§4): first-touch persist the title via <c>EnsureTitleCachedCommand</c> to obtain the internal
    /// <c>MovieId</c> (a title TMDB does not have → 404), then <c>CreateReviewCommand</c>. Under HTMX it returns
    /// the rendered <c>_ReviewCard</c> to swap into the write region; a no-JS post redirects back to the title.
    /// </summary>
    /// <param name="form">The bound create form (TMDB coordinates + rating + body; no author id).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handlers.</param>
    /// <returns>The <c>_ReviewCard</c> partial (HTMX) or a redirect to the Details page (no-JS); <c>404</c> for
    /// an unknown title.</returns>
    [HttpPost("")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Create([FromForm] CreateReviewForm form, CancellationToken cancellationToken)
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

        var result = await sender.Send(
            new CreateReviewCommand(movieId, form.Rating, form.Body), cancellationToken);

        var card = await sender.Send(new GetReviewCardQuery(result.ReviewId), cancellationToken);
        if (card is null)
        {
            return NotFound();
        }

        if (IsHtmxRequest())
        {
            return PartialView("_ReviewCard", card);
        }

        return RedirectToAction(
            "Details",
            "Discovery",
            new { media = form.Media.ToString().ToLowerInvariant(), tmdbId = form.TmdbId });
    }

    /// <summary>Returns a single review's card (<c>GET /reviews/{id}</c>) — used by the edit form's Cancel.</summary>
    /// <param name="id">The review id.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The <c>_ReviewCard</c> partial, or <c>404</c> when the review does not exist.</returns>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Card(Guid id, CancellationToken cancellationToken)
    {
        var card = await sender.Send(new GetReviewCardQuery(id), cancellationToken);
        return card is null ? NotFound() : PartialView("_ReviewCard", card);
    }

    /// <summary>
    /// Returns the inline edit form for a review (<c>GET /reviews/{id}/edit</c>). This is a presentation gate
    /// only — it 403s a non-owner here for a clean UX, but the authoritative ownership check is re-run in
    /// <c>EditReviewCommand</c> on the POST (ADR 0009).
    /// </summary>
    /// <param name="id">The review id.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The <c>_ReviewEditForm</c> partial; <c>404</c> if missing; <c>403</c> if not the owner.</returns>
    [HttpGet("{id:guid}/edit")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var card = await sender.Send(new GetReviewCardQuery(id), cancellationToken);
        if (card is null)
        {
            return NotFound();
        }

        if (!card.IsMine)
        {
            throw new ForbiddenAccessException("You can only edit your own review.");
        }

        return PartialView("_ReviewEditForm", card);
    }

    /// <summary>
    /// Applies an edit to a review (<c>POST /reviews/{id}</c>) and returns the updated card. Ownership is
    /// enforced (403) and a missing review 404s — both inside <c>EditReviewCommand</c>.
    /// </summary>
    /// <param name="id">The review id (from the route).</param>
    /// <param name="form">The bound edit form (rating + body).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The updated <c>_ReviewCard</c> partial.</returns>
    [HttpPost("{id:guid}")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Update(
        Guid id, [FromForm] EditReviewForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        await sender.Send(new EditReviewCommand(id, form.Rating, form.Body), cancellationToken);

        var card = await sender.Send(new GetReviewCardQuery(id), cancellationToken);
        return card is null ? NotFound() : PartialView("_ReviewCard", card);
    }

    /// <summary>
    /// Deletes the current user's review (<c>DELETE /reviews/{id}</c>) and returns an empty <c>200</c> so HTMX
    /// removes the node. Ownership is enforced (403) and a missing review 404s — inside
    /// <c>DeleteReviewCommand</c>; the database FK cascade removes the review's likes and comments.
    /// </summary>
    /// <param name="id">The review id.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>An empty <c>200</c>.</returns>
    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteReviewCommand(id), cancellationToken);
        return Ok();
    }

    /// <summary>
    /// Likes a review (<c>POST /reviews/{id}/like</c>) and returns the re-rendered <c>_LikeButton</c> (count +
    /// pressed state) to swap. Idempotent — a repeat like is a no-op inside <see cref="LikeReviewCommand"/>.
    /// </summary>
    /// <param name="id">The review id.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The <c>_LikeButton</c> partial.</returns>
    [HttpPost("{id:guid}/like")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Like(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new LikeReviewCommand(id), cancellationToken);
        return PartialView("_LikeButton", new LikeButtonVm(id, result.LikeCount, result.LikedByMe));
    }

    /// <summary>
    /// Unlikes a review (<c>DELETE /reviews/{id}/like</c>) and returns the re-rendered <c>_LikeButton</c>.
    /// Idempotent — unliking a review you have not liked is a no-op inside <see cref="UnlikeReviewCommand"/>.
    /// </summary>
    /// <param name="id">The review id.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The <c>_LikeButton</c> partial.</returns>
    [HttpDelete("{id:guid}/like")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Unlike(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new UnlikeReviewCommand(id), cancellationToken);
        return PartialView("_LikeButton", new LikeButtonVm(id, result.LikeCount, result.LikedByMe));
    }

    /// <summary>
    /// The "your review" fragment for the Details page (<c>GET /reviews/mine?tmdbId=&amp;media=</c>): returns
    /// the current user's existing review card, or the write form when they have none (or the title is not yet
    /// cached). Drives the create-vs-edit affordance without a full page reload.
    /// </summary>
    /// <param name="tmdbId">The title's TMDB id.</param>
    /// <param name="media">The title's media type.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The <c>_ReviewCard</c> (has a review) or <c>_ReviewWriteForm</c> (none); <c>404</c> for bad input.</returns>
    [HttpGet("mine")]
    public async Task<IActionResult> Mine(int tmdbId, MediaType media, CancellationToken cancellationToken)
    {
        if (tmdbId <= 0 || !Enum.IsDefined(media))
        {
            return NotFound();
        }

        try
        {
            var movieId = await sender.Send(new GetCachedMovieIdQuery(tmdbId, media), cancellationToken);

            var mine = movieId is { } id
                ? await sender.Send(new GetMyReviewForTitleQuery(id), cancellationToken)
                : null;

            return mine is not null
                ? PartialView("_ReviewCard", mine)
                : PartialView("_ReviewWriteForm", new ReviewWriteFormVm(tmdbId, media));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A lazy region must NOT surface a 500 — HTMX will not swap a non-2xx, so the skeleton would
            // shimmer forever (the 2.3 lesson). Log a Warning and return the on-brand retry partial (HTTP 200)
            // whose Retry re-fires this same lazy load. Client aborts are left to propagate (no one is waiting).
            LogMyReviewLoadFailed(logger, ex, tmdbId);
            var retryUrl = Url.Action("Mine", "Reviews", new { tmdbId, media }) ?? string.Empty;
            return PartialView("_ReviewsError", new ReviewsErrorVm(retryUrl));
        }
    }

    // HTMX sets the "HX-Request" header on every AJAX request; its absence means a plain (no-JS) form post.
    private bool IsHtmxRequest() => Request.Headers.ContainsKey("HX-Request");

    [LoggerMessage(
        EventId = 3100,
        Level = LogLevel.Warning,
        Message = "Loading the current user's review for TMDB id {TmdbId} failed; serving the retry partial (HTTP 200).")]
    private static partial void LogMyReviewLoadFailed(ILogger logger, Exception exception, int tmdbId);
}
