using Cinora.Application.Features.Friends;
using Cinora.Application.Features.Reviews;
using Cinora.Domain.Enums;

namespace Cinora.Application.Features.Profiles;

/// <summary>
/// The current viewer's relationship to the profile owner, which drives BOTH what the profile reveals (§7.3)
/// and which friendship control it renders (Add friend / Respond / Requested / Remove).
/// </summary>
public enum ProfileRelationship
{
    /// <summary>The viewer is the profile owner (they see everything, including their own reviews and friends).</summary>
    Owner = 0,

    /// <summary>The viewer and the owner are accepted friends (full profile).</summary>
    Friend = 1,

    /// <summary>The owner has sent the viewer a pending request — the viewer may respond to it.</summary>
    RequestIncoming = 2,

    /// <summary>The viewer has sent the owner a pending request, awaiting a response.</summary>
    RequestOutgoing = 3,

    /// <summary>No active relationship — the viewer may send a friend request.</summary>
    None = 4,

    /// <summary>The viewer has blocked the owner — they see only an Unblock control, no content (§2, §7).</summary>
    BlockedByMe = 5,
}

/// <summary>
/// A privacy-shaped profile (§7.3). The <see cref="GetProfileQuery"/> handler resolves the viewer's
/// <see cref="Relationship"/> and populates ONLY the fields that relationship permits — reviews and the friends
/// list are never fetched-then-hidden, so privacy is enforced at the data read, not in the template. An
/// owner/accepted-friend sees the full profile (reviews + friends); a signed-in non-friend sees the public
/// profile (reviews) when the owner is public, or a limited profile (name + avatar only) when private.
/// </summary>
public sealed record ProfileVm
{
    /// <summary>The profile owner's user id.</summary>
    public required Guid UserId { get; init; }

    /// <summary>The profile owner's public display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The profile owner's avatar storage key, or <c>null</c> when none is set (render a default avatar).</summary>
    public string? AvatarFileKey { get; init; }

    /// <summary>Whether the owner's profile is publicly visible (governs the non-friend view).</summary>
    public required bool IsProfilePublic { get; init; }

    /// <summary>The viewer's relationship to the owner (drives visibility and the friendship control).</summary>
    public required ProfileRelationship Relationship { get; init; }

    /// <summary>
    /// Whether the viewer may see the owner's watchlist highlights — the SAME §7.3 tier as
    /// <see cref="CanViewReviews"/> (owner, accepted friend, or a public profile). A limited (private,
    /// non-friend) view is <c>false</c> and <see cref="WatchlistHighlights"/> is never fetched.
    /// </summary>
    public required bool CanViewWatchlist { get; init; }

    /// <summary>
    /// The id of the owner's pending request TO the viewer, when <see cref="Relationship"/> is
    /// <see cref="ProfileRelationship.RequestIncoming"/> — the accept/decline actions target it. <c>null</c>
    /// otherwise.
    /// </summary>
    public Guid? IncomingRequestId { get; init; }

    /// <summary>Whether the viewer may see the owner's reviews (owner, accepted friend, or a public profile).</summary>
    public required bool CanViewReviews { get; init; }

    /// <summary>Whether the viewer may see the owner's friends list (owner or accepted friend only).</summary>
    public required bool CanViewFriends { get; init; }

    /// <summary>The owner's friend count, shown when the profile is visible (full or public); <c>0</c> when limited.</summary>
    public required int FriendCount { get; init; }

    /// <summary>The owner's recent reviews (viewer-relative flags), populated only when <see cref="CanViewReviews"/>.</summary>
    public required IReadOnlyList<ReviewVm> Reviews { get; init; }

    /// <summary>The owner's friends, populated only when <see cref="CanViewFriends"/>.</summary>
    public required IReadOnlyList<FriendVm> Friends { get; init; }

    /// <summary>
    /// The owner's most recent Watched titles (a "what they've seen" strip), populated only when
    /// <see cref="CanViewWatchlist"/>; an empty list when the viewer may not see the watchlist or the owner has
    /// none. Never fetched-then-hidden — privacy is enforced at the data read (§7).
    /// </summary>
    public required IReadOnlyList<WatchlistHighlightVm> WatchlistHighlights { get; init; }
}

/// <summary>
/// One title on a profile's watchlist-highlights strip (Milestone 4.5, §7) — a recent Watched title projected
/// for a premium poster card. Presentation-ready primitives only (no Domain entity, no EF type), so it is safe
/// to hand straight to the Razor strip, which links each poster to the Details route
/// <c>/discover/title/{media}/{tmdbId}</c>.
/// </summary>
/// <param name="TmdbId">The title's TMDB identifier (for the Details link).</param>
/// <param name="Media">Whether the title is a movie or a series (drives the Details route segment).</param>
/// <param name="Title">The title's display title (the poster's accessible name).</param>
/// <param name="PosterPath">The title's raw TMDB poster path, or <c>null</c> (the strip renders a placeholder).</param>
public sealed record WatchlistHighlightVm(int TmdbId, MediaType Media, string Title, string? PosterPath);
