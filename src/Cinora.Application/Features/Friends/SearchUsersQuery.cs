using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Blocks;
using Cinora.Application.Features.Profiles;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Friends;

/// <summary>
/// Finds a single user by an EXACT username or EXACT email (§4). A term containing '@' is treated as an email
/// (resolved via <see cref="IUserDirectory"/>); otherwise it is matched against the unique
/// <c>User.NormalizedDisplayName</c> using the SAME normalization the domain applies (<see cref="User.Normalize"/>).
/// The current user and anyone in a block relationship with them (either direction) are excluded, and a no-match
/// returns an empty result — indistinguishable from a blocked user, so a block is never revealed (§14).
/// </summary>
/// <param name="Term">The full username or full email to look up.</param>
public sealed record SearchUsersQuery(string Term) : IRequest<UserSearchVm>;

/// <summary>The result of a <see cref="SearchUsersQuery"/>: the single match, or <c>null</c> when none.</summary>
/// <param name="Match">The matched user projected for a person card, or <c>null</c>.</param>
public sealed record UserSearchVm(UserSearchResultVm? Match);

/// <summary>One person-card result with the viewer-relative relationship control state.</summary>
/// <param name="UserId">The matched user's id.</param>
/// <param name="DisplayName">The matched user's display name (Razor-encoded on render).</param>
/// <param name="AvatarFileKey">The matched user's avatar key, or <c>null</c>.</param>
/// <param name="Relationship">The viewer's relationship (drives Add friend / Requested / Respond / Friends).
/// Never <see cref="ProfileRelationship.BlockedByMe"/> — blocked users are excluded from results entirely.</param>
public sealed record UserSearchResultVm(
    Guid UserId, string DisplayName, string? AvatarFileKey, ProfileRelationship Relationship);

/// <summary>Validates <see cref="SearchUsersQuery"/>: a non-blank, length-bounded term.</summary>
public sealed class SearchUsersQueryValidator : AbstractValidator<SearchUsersQuery>
{
    /// <summary>Configures the rules for <see cref="SearchUsersQuery"/>.</summary>
    public SearchUsersQueryValidator() =>
        RuleFor(query => query.Term)
            .NotEmpty().WithMessage("Enter a username or email.")
            .MaximumLength(256).WithMessage("That search term is too long.");
}

/// <summary>
/// Handles <see cref="SearchUsersQuery"/>. Resolves the exact candidate (email → <see cref="IUserDirectory"/>;
/// username → the unique <c>NormalizedDisplayName</c> seek), drops self and any block-related id
/// (<see cref="BlockQueries.BlockedOrBlockedByIdsAsync"/>), then projects the match with the viewer's
/// relationship. Returns an empty <see cref="UserSearchVm"/> for a no-match, a self-match, or a blocked user —
/// the three are indistinguishable to the caller (§14).
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved viewer (§2, ADR 0009).</param>
/// <param name="directory">The Identity-backed exact-email resolver (§4.2).</param>
public sealed class SearchUsersQueryHandler(IAppDbContext db, ICurrentUser currentUser, IUserDirectory directory)
    : IRequestHandler<SearchUsersQuery, UserSearchVm>
{
    /// <inheritdoc />
    public async Task<UserSearchVm> Handle(SearchUsersQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();
        var term = request.Term.Trim();

        Guid? candidateId;
        if (term.Contains('@', StringComparison.Ordinal))
        {
            candidateId = await directory.FindUserIdByEmailAsync(term, cancellationToken);
        }
        else
        {
            // Normalize with the SAME rule the domain stores (trim + upper-invariant) so an exact display-name
            // search seeks the unique index. Computed here (not inside the expression) so EF parameterizes it.
            var normalized = User.Normalize(term);
            candidateId = await db.Users
                .AsNoTracking()
                .Where(user => user.NormalizedDisplayName == normalized)
                .Select(user => (Guid?)user.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (candidateId is null || candidateId == me)
        {
            return new UserSearchVm(null); // no match, or yourself
        }

        var excluded = await BlockQueries.BlockedOrBlockedByIdsAsync(db, me, cancellationToken);
        if (excluded.Contains(candidateId.Value))
        {
            return new UserSearchVm(null); // blocked either way — indistinguishable from not-found (§14)
        }

        var owner = await db.Users
            .AsNoTracking()
            .Where(user => user.Id == candidateId.Value)
            .Select(user => new { user.Id, user.DisplayName, user.AvatarFileKey })
            .FirstOrDefaultAsync(cancellationToken);
        if (owner is null)
        {
            return new UserSearchVm(null);
        }

        var relationship = await ResolveRelationshipAsync(me, owner.Id, cancellationToken);
        return new UserSearchVm(new UserSearchResultVm(owner.Id, owner.DisplayName, owner.AvatarFileKey, relationship));
    }

    // The viewer→match relationship for the card's control. Block states cannot occur (excluded above).
    private async Task<ProfileRelationship> ResolveRelationshipAsync(
        Guid me, Guid otherId, CancellationToken cancellationToken)
    {
        var pairRows = await db.Friends
            .AsNoTracking()
            .Where(friend => (friend.RequesterId == me && friend.AddresseeId == otherId)
                || (friend.RequesterId == otherId && friend.AddresseeId == me))
            .Select(friend => new { friend.RequesterId, friend.Status })
            .ToListAsync(cancellationToken);

        if (pairRows.Exists(row => row.Status == FriendStatus.Accepted))
        {
            return ProfileRelationship.Friend;
        }

        if (pairRows.Exists(row => row.Status == FriendStatus.Pending && row.RequesterId == otherId))
        {
            return ProfileRelationship.RequestIncoming;
        }

        if (pairRows.Exists(row => row.Status == FriendStatus.Pending && row.RequesterId == me))
        {
            return ProfileRelationship.RequestOutgoing;
        }

        return ProfileRelationship.None;
    }
}
