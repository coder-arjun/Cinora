using Cinora.Application.Features.Blocks;
using Cinora.Application.Features.Friends;

namespace Cinora.Web.ViewModels.Friends;

/// <summary>
/// The form posted to <c>POST /friends/requests</c> to send a friend request (e.g. from a profile's "Add
/// friend" control). It carries ONLY the addressee's id (the resource); the requester is server-resolved
/// (ADR 0009), so a spoofed requester field would simply be unbound and ignored.
/// </summary>
public sealed class SendFriendRequestForm
{
    /// <summary>The id of the user to send a friend request to.</summary>
    public Guid AddresseeUserId { get; set; }
}

/// <summary>
/// The model for the <c>/friends</c> hub: the current user's accepted friends, their incoming and outgoing
/// pending requests, and the users they have blocked, ready to render as HTMX-swappable rows.
/// </summary>
/// <param name="Friends">The current user's accepted friends.</param>
/// <param name="Requests">The current user's pending requests (incoming + outgoing).</param>
/// <param name="Blocked">The users the current user has blocked (the Blocked section).</param>
public sealed record FriendsPageVm(FriendsVm Friends, PendingRequestsVm Requests, BlockedUsersVm Blocked);

/// <summary>
/// The model for the <c>_FriendActionResult</c> partial: a short, on-brand status line an HTMX write swaps in
/// (send/accept/decline). The message is server-composed and Razor output-encoded.
/// </summary>
/// <param name="Message">The status text to show (e.g. "Friend request sent.").</param>
public sealed record FriendActionResultVm(string Message);
