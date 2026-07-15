namespace Cinora.Application.Features.Comments;

/// <summary>
/// A single comment on a review, projected for display: its author (display name + optional avatar key), the
/// plain-text body, the created instant, and the per-viewer <c>IsMine</c> flag. Carries only presentation-ready
/// primitives — no Domain entity, no EF type. The body is rendered PLAIN TEXT (Razor output-encoded, CSS
/// <c>white-space: pre-wrap</c> for line breaks); it is never treated as HTML (phase-3 XSS stance §3).
/// </summary>
public sealed record CommentVm
{
    /// <summary>The comment's unique identifier.</summary>
    public required Guid CommentId { get; init; }

    /// <summary>The id of the review this comment belongs to.</summary>
    public required Guid ReviewId { get; init; }

    /// <summary>The author's user id.</summary>
    public required Guid AuthorUserId { get; init; }

    /// <summary>The author's public display name.</summary>
    public required string AuthorDisplayName { get; init; }

    /// <summary>The author's avatar storage key, or <c>null</c> when none is set (render a default avatar).</summary>
    public string? AuthorAvatarFileKey { get; init; }

    /// <summary>The plain-text comment body (rendered Razor-encoded; never as HTML).</summary>
    public required string Body { get; init; }

    /// <summary>The UTC instant the comment was created.</summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>
    /// Whether the current user authored this comment — drives the delete affordance in the UI. Presentation
    /// sugar only; the server re-checks ownership in <c>DeleteCommentCommand</c>.
    /// </summary>
    public required bool IsMine { get; init; }
}

/// <summary>
/// An opaque keyset cursor into a review's comment thread, ordered oldest-first by
/// <c>(CreatedAtUtc ASC, Id ASC)</c>. A page returns the cursor of its last item; the next request fetches
/// strictly-newer rows. Never a page number and never an <c>OFFSET</c> (§13).
/// </summary>
/// <param name="CreatedAtUtc">The <see cref="CommentVm.CreatedAtUtc"/> of the last item on the current page.</param>
/// <param name="Id">The <see cref="CommentVm.CommentId"/> of the last item on the current page (the tie-breaker).</param>
public sealed record CommentCursor(DateTime CreatedAtUtc, Guid Id);
