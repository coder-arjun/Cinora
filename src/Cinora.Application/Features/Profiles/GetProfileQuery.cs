using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Blocks;
using Cinora.Application.Features.Friends;
using Cinora.Application.Features.Reviews;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Profiles;

/// <summary>
/// Fetches a privacy-shaped profile for the given user as seen by the current (authenticated) viewer (§7.3).
/// Profiles require authentication in Phase 3 — anonymous access is handled by the <c>[Authorize]</c> controller
/// (a redirect to login), so this handler always has a current user. It resolves the viewer's relationship to
/// the owner and PROJECTS ONLY the permitted fields: reviews and the friends list are fetched only when the
/// relationship allows, never fetched-then-hidden.
/// </summary>
/// <param name="UserId">The id of the profile owner to view.</param>
public sealed record GetProfileQuery(Guid UserId) : IRequest<ProfileVm>;

/// <summary>
/// Handles <see cref="GetProfileQuery"/>: resolve the owner (404 if missing), determine the viewer's
/// <see cref="ProfileRelationship"/> from the directed <c>Friend</c> graph, then load reviews (when the viewer
/// may see them) and the friends list (owner/friend only), plus a friend count for any visible profile. Reviews
/// reuse the shared <see cref="ReviewProjection"/> with the viewer's id, so like/edit affordances are correct.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved viewer (§2, ADR 0009).</param>
public sealed class GetProfileQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetProfileQuery, ProfileVm>
{
    /// <summary>The number of recent reviews surfaced on a profile (newest-first).</summary>
    public const int ReviewsPageSize = 20;

    /// <summary>The number of recent Watched titles surfaced as watchlist highlights (newest-added-first, §7).</summary>
    public const int HighlightsCount = 8;

    /// <inheritdoc />
    public async Task<ProfileVm> Handle(GetProfileQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();
        var ownerId = request.UserId;

        var owner = await db.Users
            .AsNoTracking()
            .Where(user => user.Id == ownerId)
            .Select(user => new { user.DisplayName, user.AvatarFileKey, user.IsProfilePublic })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"User ({ownerId}) was not found.");

        // Block enforcement (§2.2, §7). Only relevant when viewing someone else.
        if (ownerId != me)
        {
            var ownerBlocksMe = await db.UserBlocks.AsNoTracking()
                .AnyAsync(block => block.BlockerId == ownerId && block.BlockedUserId == me, cancellationToken);
            if (ownerBlocksMe)
            {
                // The blocked viewer cannot see the owner at all — same 404 as a non-existent user (no leak).
                throw new NotFoundException($"User ({ownerId}) was not found.");
            }

            var iBlockOwner = await db.UserBlocks.AsNoTracking()
                .AnyAsync(block => block.BlockerId == me && block.BlockedUserId == ownerId, cancellationToken);
            if (iBlockOwner)
            {
                // The blocker sees a minimal "you blocked this user" profile with an Unblock control only.
                return new ProfileVm
                {
                    UserId = ownerId,
                    DisplayName = owner.DisplayName,
                    AvatarFileKey = owner.AvatarFileKey,
                    IsProfilePublic = owner.IsProfilePublic,
                    Relationship = ProfileRelationship.BlockedByMe,
                    IncomingRequestId = null,
                    CanViewReviews = false,
                    CanViewWatchlist = false,
                    CanViewFriends = false,
                    FriendCount = 0,
                    Reviews = [],
                    Friends = [],
                    WatchlistHighlights = [],
                };
            }
        }

        var (relationship, incomingRequestId) = await ResolveRelationshipAsync(me, ownerId, cancellationToken);

        var isFullView = relationship is ProfileRelationship.Owner or ProfileRelationship.Friend;
        var canViewReviews = isFullView || owner.IsProfilePublic;

        var reviews = canViewReviews
            ? await LoadReviewsAsync(ownerId, me, cancellationToken)
            : [];

        // Watchlist highlights ride the SAME §7.3 tier as reviews (owner / accepted friend / public profile).
        // The read runs ONLY inside this branch, so a limited (private, non-friend) view never fetches them —
        // privacy at the data read, never fetched-then-hidden, exactly as reviews above (§7, PH1).
        var canViewWatchlist = canViewReviews;
        var highlights = canViewWatchlist
            ? await LoadWatchlistHighlightsAsync(ownerId, cancellationToken)
            : [];

        var friends = isFullView
            ? await FriendProjections.AcceptedFriendsAsync(db, ownerId, cancellationToken)
            : (IReadOnlyList<FriendVm>)[];

        // A visible profile (full or public) shows the friend count; a limited profile reveals nothing.
        var friendCount = isFullView
            ? friends.Count
            : canViewReviews
                ? await FriendProjections.CountAcceptedFriendsAsync(db, ownerId, cancellationToken)
                : 0;

        return new ProfileVm
        {
            UserId = ownerId,
            DisplayName = owner.DisplayName,
            AvatarFileKey = owner.AvatarFileKey,
            IsProfilePublic = owner.IsProfilePublic,
            Relationship = relationship,
            IncomingRequestId = incomingRequestId,
            CanViewReviews = canViewReviews,
            CanViewWatchlist = canViewWatchlist,
            CanViewFriends = isFullView,
            FriendCount = friendCount,
            Reviews = reviews,
            Friends = friends,
            WatchlistHighlights = highlights,
        };
    }

    // Determines the viewer's relationship to the owner from the directed Friend graph. Priority: self → owner;
    // an accepted row (either direction) → friend; a pending them→me → incoming (with its id, for accept/decline);
    // a pending me→them → outgoing; otherwise none (including a purely declined history).
    private async Task<(ProfileRelationship Relationship, Guid? IncomingRequestId)> ResolveRelationshipAsync(
        Guid me, Guid ownerId, CancellationToken cancellationToken)
    {
        if (ownerId == me)
        {
            return (ProfileRelationship.Owner, null);
        }

        var pairRows = await db.Friends
            .AsNoTracking()
            .Where(friend => (friend.RequesterId == me && friend.AddresseeId == ownerId)
                || (friend.RequesterId == ownerId && friend.AddresseeId == me))
            .Select(friend => new { friend.Id, friend.RequesterId, friend.Status })
            .ToListAsync(cancellationToken);

        if (pairRows.Exists(row => row.Status == FriendStatus.Accepted))
        {
            return (ProfileRelationship.Friend, null);
        }

        var incoming = pairRows.Find(row => row.Status == FriendStatus.Pending && row.RequesterId == ownerId);
        if (incoming is not null)
        {
            return (ProfileRelationship.RequestIncoming, incoming.Id);
        }

        if (pairRows.Exists(row => row.Status == FriendStatus.Pending && row.RequesterId == me))
        {
            return (ProfileRelationship.RequestOutgoing, null);
        }

        return (ProfileRelationship.None, null);
    }

    private async Task<IReadOnlyList<ReviewVm>> LoadReviewsAsync(
        Guid ownerId, Guid viewerId, CancellationToken cancellationToken)
    {
        var rows = await db.Reviews
            .AsNoTracking()
            .Where(review => review.UserId == ownerId)
            .OrderByDescending(review => review.CreatedAtUtc)
            .ThenByDescending(review => review.Id)
            .Take(ReviewsPageSize)
            .Select(ReviewProjection.ToRow(db, viewerId))
            .ToListAsync(cancellationToken);

        return rows.Select(row => ReviewProjection.ToVm(row, viewerId)).ToList();
    }

    // Loads up to HighlightsCount of the owner's most recently ADDED Watched titles — a "what they've seen"
    // strip (§7). ONLY called from inside the canViewWatchlist branch, so a limited tier never runs it. The
    // ORDER BY + Take stay on the OUTER Watchlists query so the newest-first order is preserved; the title
    // fields are resolved by ONE correlated sub-query joining Movies by PK (required Restrict FK → always
    // present), so the read is N+1-free — the same shape GetMyWatchlistQuery proves (§12). The
    // (UserId, Status, AddedAtUtc DESC, Id DESC) keyset is served by IX_Watchlists_UserId_Status_AddedAtUtc
    // (4.4, §10) — no new index.
    private async Task<IReadOnlyList<WatchlistHighlightVm>> LoadWatchlistHighlightsAsync(
        Guid ownerId, CancellationToken cancellationToken)
    {
        var rows = await db.Watchlists
            .AsNoTracking()
            .Where(entry => entry.UserId == ownerId && entry.Status == WatchlistStatus.Watched)
            .OrderByDescending(entry => entry.AddedAtUtc)
            .ThenByDescending(entry => entry.Id)
            .Take(HighlightsCount)
            .Select(entry => db.Movies
                .Where(movie => movie.Id == entry.MovieId)
                .Select(movie => new WatchlistHighlightVm(
                    movie.TmdbId, movie.MediaType, movie.Title, movie.PosterPath))
                .FirstOrDefault())
            .ToListAsync(cancellationToken);

        // The required Restrict FK guarantees the title exists; the null filter is belt-and-suspenders.
        return rows.Where(highlight => highlight is not null).Select(highlight => highlight!).ToList();
    }
}
