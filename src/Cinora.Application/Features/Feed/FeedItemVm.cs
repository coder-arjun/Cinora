using Cinora.Domain.Enums;

namespace Cinora.Application.Features.Feed;

/// <summary>
/// One activity in the friends feed: a friend's review of a title, projected for display. It carries the
/// author (display name + optional avatar key), the reviewed title's TMDB coordinates and poster (so the card
/// can link to the Details page and show a thumbnail), the 1–10 rating, a truncated body excerpt, aggregate
/// like/comment counts, and the per-viewer <c>LikedByMe</c> flag. Presentation-ready primitives only — no
/// Domain entity and no EF type — so it is safe to hand straight to a Razor partial. The excerpt is rendered
/// PLAIN TEXT (Razor output-encoded); it is never treated as HTML (phase-3 XSS stance §3). Feed v1 is
/// reviews-only (ADR 0012 §3): the counts are shown for signal, but the feed card exposes no like/comment
/// actions of its own.
/// </summary>
public sealed record FeedItemVm
{
    /// <summary>The reviewed review's unique identifier (also the keyset tie-breaker).</summary>
    public required Guid ReviewId { get; init; }

    /// <summary>The friend/author's user id (drives the profile link).</summary>
    public required Guid AuthorUserId { get; init; }

    /// <summary>The author's public display name.</summary>
    public required string AuthorDisplayName { get; init; }

    /// <summary>The author's avatar storage key, or <c>null</c> when none is set (render a default avatar).</summary>
    public string? AuthorAvatarFileKey { get; init; }

    /// <summary>The reviewed title's TMDB identifier (for the Details link).</summary>
    public required int MovieTmdbId { get; init; }

    /// <summary>Whether the reviewed title is a movie or a series (for the Details link segment).</summary>
    public required MediaType MovieMediaType { get; init; }

    /// <summary>The reviewed title's display title.</summary>
    public required string MovieTitle { get; init; }

    /// <summary>The reviewed title's raw TMDB poster path, or <c>null</c> (render the placeholder).</summary>
    public string? MoviePosterPath { get; init; }

    /// <summary>The 1–10 rating (the underlying value of the domain <c>Rating</c> value object).</summary>
    public required int Rating { get; init; }

    /// <summary>The review body truncated to a short excerpt (rendered Razor-encoded; never as HTML).</summary>
    public required string BodyExcerpt { get; init; }

    /// <summary>The UTC instant the review was created (also the keyset sort key).</summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>The number of likes recorded against the review.</summary>
    public required int LikeCount { get; init; }

    /// <summary>The number of comments recorded against the review.</summary>
    public required int CommentCount { get; init; }

    /// <summary>Whether the current (viewing) user has liked the review; <c>false</c> when anonymous.</summary>
    public required bool LikedByMe { get; init; }
}

/// <summary>
/// An opaque keyset cursor into the activity feed, ordered newest-first by <c>(CreatedAtUtc DESC, Id DESC)</c>.
/// A page returns the cursor of its last item; the next request fetches strictly-older rows. Never a page
/// number and never an <c>OFFSET</c> (deep-pagination cost — §13, ADR 0012).
/// </summary>
/// <param name="CreatedAtUtc">The <see cref="FeedItemVm.CreatedAtUtc"/> of the last item on the current page.</param>
/// <param name="Id">The <see cref="FeedItemVm.ReviewId"/> of the last item on the current page (the tie-breaker).</param>
public sealed record FeedCursor(DateTime CreatedAtUtc, Guid Id);

/// <summary>One keyset page of the current user's friends feed.</summary>
/// <param name="Items">The feed items on this page, newest-first.</param>
/// <param name="NextCursor">The cursor to fetch the next page, or <c>null</c> when there is no next page.</param>
/// <param name="HasMore">Whether a further page exists.</param>
/// <param name="HasFriends">
/// Whether the current user has any accepted friends — distinguishes the two empty states: <c>false</c> with no
/// items is the "find friends" state, <c>true</c> with no items is the quiet "no recent activity" state.
/// </param>
public sealed record ActivityFeedVm(
    IReadOnlyList<FeedItemVm> Items,
    FeedCursor? NextCursor,
    bool HasMore,
    bool HasFriends);
