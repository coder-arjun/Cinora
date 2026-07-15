using Cinora.Application.Features.Reviews;
using Cinora.Domain.Enums;

namespace Cinora.Web.ViewModels.Reviews;

/// <summary>
/// The form posted to <c>POST /reviews</c> to create a review from the public Details page. It carries the
/// title's TMDB coordinates (the controller resolves them to the internal <c>MovieId</c> via
/// <c>EnsureTitleCachedCommand</c> — §4) plus the rating and body. It deliberately carries NO author id: the
/// acting user is resolved server-side (ADR 0009), so a spoofed <c>UserId</c> field would simply be unbound
/// and ignored (test R1).
/// </summary>
public sealed class CreateReviewForm
{
    /// <summary>The TMDB identifier of the reviewed title.</summary>
    public int TmdbId { get; set; }

    /// <summary>Whether the title is a movie or a series.</summary>
    public MediaType Media { get; set; }

    /// <summary>The 1–10 rating (validated by <c>CreateReviewCommandValidator</c>).</summary>
    public int Rating { get; set; }

    /// <summary>The plain-text review body.</summary>
    public string Body { get; set; } = string.Empty;
}

/// <summary>The form posted to <c>POST /reviews/{id}</c> to edit a review (the review id comes from the route).</summary>
public sealed class EditReviewForm
{
    /// <summary>The new 1–10 rating.</summary>
    public int Rating { get; set; }

    /// <summary>The new plain-text review body.</summary>
    public string Body { get; set; } = string.Empty;
}

/// <summary>The model for the "write your review" form partial — the TMDB coordinates it posts back.</summary>
/// <param name="TmdbId">The TMDB identifier of the title being reviewed.</param>
/// <param name="Media">Whether the title is a movie or a series.</param>
public sealed record ReviewWriteFormVm(int TmdbId, MediaType Media);

/// <summary>
/// The model for the public reviews-list partials (<c>_ReviewList</c> / <c>_ReviewListPage</c>): the
/// Application page plus the title's TMDB coordinates, which the load-more sentinel needs to build the
/// next-page URL (the public list is addressed by <c>(media, tmdbId)</c>, not the internal id).
/// </summary>
/// <param name="Reviews">The Application keyset page of reviews.</param>
/// <param name="Media">The title's media type (for the sentinel URL).</param>
/// <param name="TmdbId">The title's TMDB id (for the sentinel URL).</param>
public sealed record ReviewListVm(TitleReviewsVm Reviews, MediaType Media, int TmdbId);

/// <summary>
/// The model for the <c>_ReviewsError</c> partial: the on-brand "couldn't load reviews" state served with
/// HTTP 200 (so HTMX swaps it in place of the shimmering skeleton — it never swaps a non-2xx) when a lazy
/// review read fails after the read pipeline. Its Retry control re-fires the exact same lazy <c>hx-get</c>.
/// </summary>
/// <param name="RetryUrl">The URL the Retry control re-GETs — the same lazy read that just failed.</param>
public sealed record ReviewsErrorVm(string RetryUrl);

/// <summary>
/// The model for the <c>_LikeButton</c> partial (Milestone 3.2): the review id (to build the like/unlike URL)
/// plus the post-write count + pressed state. The button toggles itself via <c>hx-post</c>/<c>hx-delete</c>
/// and swaps its own <c>outerHTML</c> with the re-rendered partial.
/// </summary>
/// <param name="ReviewId">The review the like button acts on.</param>
/// <param name="LikeCount">The review's current like count.</param>
/// <param name="LikedByMe">Whether the current user likes the review (drives the verb + pressed state).</param>
public sealed record LikeButtonVm(Guid ReviewId, int LikeCount, bool LikedByMe);

/// <summary>The model for the <c>_CommentWriteForm</c> partial: the review the new comment posts to.</summary>
/// <param name="ReviewId">The review being commented on.</param>
public sealed record CommentWriteFormVm(Guid ReviewId);

/// <summary>The form posted to <c>POST /reviews/{id}/comments</c> to add a comment (the review id comes from the route).</summary>
public sealed class CommentForm
{
    /// <summary>The plain-text comment body (validated by <c>AddCommentCommandValidator</c>).</summary>
    public string Body { get; set; } = string.Empty;
}
