namespace Cinora.Application.Features.Reviews;

/// <summary>
/// A single review projected for display: its author (display name + optional avatar key), the 1–10 rating,
/// the plain-text body, the created/edited instants, aggregate like/comment counts, and the two per-viewer
/// affordance flags. Carries only presentation-ready primitives — no Domain entity and no EF type — so it is
/// safe to hand straight to a Razor partial. The body is rendered PLAIN TEXT (Razor output-encoded, CSS
/// <c>white-space: pre-wrap</c> for line breaks); it is never treated as HTML (phase-3 XSS stance §3).
/// </summary>
public sealed record ReviewVm
{
    /// <summary>The review's unique identifier.</summary>
    public required Guid ReviewId { get; init; }

    /// <summary>The author's user id (drives the profile link and the ownership comparison).</summary>
    public required Guid AuthorUserId { get; init; }

    /// <summary>The author's public display name.</summary>
    public required string AuthorDisplayName { get; init; }

    /// <summary>The author's avatar storage key, or <c>null</c> when none is set (render a default avatar).</summary>
    public string? AuthorAvatarFileKey { get; init; }

    /// <summary>The 1–10 rating (the underlying value of the domain <c>Rating</c> value object).</summary>
    public required int Rating { get; init; }

    /// <summary>The plain-text review body (rendered Razor-encoded; never as HTML).</summary>
    public required string Body { get; init; }

    /// <summary>The UTC instant the review was created.</summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>The UTC instant the review was last edited, or <c>null</c> if never edited.</summary>
    public DateTime? UpdatedAtUtc { get; init; }

    /// <summary>The number of likes recorded against this review.</summary>
    public required int LikeCount { get; init; }

    /// <summary>The number of comments recorded against this review.</summary>
    public required int CommentCount { get; init; }

    /// <summary>Whether the current (viewing) user has liked this review; <c>false</c> when anonymous.</summary>
    public required bool LikedByMe { get; init; }

    /// <summary>
    /// Whether the current user is the review's author — drives the edit/delete affordances in the UI. This
    /// is presentation sugar only; the server re-checks ownership in the edit/delete command handlers.
    /// </summary>
    public required bool IsMine { get; init; }
}

/// <summary>
/// An opaque keyset cursor into a title's reviews list, ordered newest-first by
/// <c>(CreatedAtUtc DESC, Id DESC)</c>. A page returns the cursor of its last item; the next request fetches
/// strictly-older rows. Never a page number and never an <c>OFFSET</c> (deep-pagination cost — §13).
/// </summary>
/// <param name="CreatedAtUtc">The <see cref="ReviewVm.CreatedAtUtc"/> of the last item on the current page.</param>
/// <param name="Id">The <see cref="ReviewVm.ReviewId"/> of the last item on the current page (the tie-breaker).</param>
public sealed record ReviewCursor(DateTime CreatedAtUtc, Guid Id);
