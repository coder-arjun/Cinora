using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Blocks;
using Cinora.Application.Features.Friends;
using Cinora.Application.Features.Profiles;
using Cinora.Web.Infrastructure;
using Cinora.Web.ViewModels.Friends;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cinora.Web.Controllers;

/// <summary>
/// The authenticated friends surface (Milestone 3.3): the friends + requests page, sending a request, and
/// responding to or removing one. Every action requires authentication (the class is
/// <see cref="AuthorizeAttribute"/>; the global anti-forgery filter validates the token every unsafe verb
/// carries via the <c>RequestVerificationToken</c> header / hidden field). The controller stays thin: it
/// model-binds, dispatches via <see cref="ISender"/>, and returns an HTMX partial — the directed-pair guard,
/// ownership (addressee-only response), and notification co-persist all live in the handlers (§7, ADR 0009).
/// The acting user is server-resolved via <c>ICurrentUser</c>, so no requester id is ever bound.
/// </summary>
/// <param name="sender">The hand-rolled mediator used to dispatch the friend commands/queries.</param>
[Authorize]
[Route("friends")]
public sealed class FriendsController(ISender sender) : Controller
{
    /// <summary>
    /// The friends page (<c>GET /friends</c>): the current user's accepted friends plus incoming/outgoing
    /// pending requests.
    /// </summary>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handlers.</param>
    /// <returns>The <c>Index</c> view bound to the friends + requests page model.</returns>
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var friends = await sender.Send(new GetFriendsQuery(), cancellationToken);
        var requests = await sender.Send(new GetPendingRequestsQuery(), cancellationToken);
        var blocked = await sender.Send(new GetBlockedUsersQuery(), cancellationToken);
        return View(new FriendsPageVm(friends, requests, blocked));
    }

    /// <summary>
    /// Exact-match people search (<c>GET /friends/search?term=</c>) for the Find-people hub (§4, §6). Returns a
    /// person card for the single match or an empty state; a blocked/no match are identical (§14). Rate-limited
    /// to blunt enumeration.
    /// </summary>
    /// <param name="term">The full username or full email to look up.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The <c>_UserSearchResult</c> partial.</returns>
    [HttpGet("search")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Search([FromQuery] string? term, CancellationToken cancellationToken)
    {
        var result = string.IsNullOrWhiteSpace(term)
            ? new UserSearchVm(null)
            : await sender.Send(new SearchUsersQuery(term), cancellationToken);
        return PartialView("_UserSearchResult", result);
    }

    /// <summary>
    /// Sends a friend request (<c>POST /friends/requests</c>) and returns a status partial reflecting the
    /// directed-pair guard outcome (§7.2). A profile's "Add friend" control swaps itself with the result.
    /// </summary>
    /// <param name="form">The bound form carrying the addressee's id (no requester id).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The <c>_FriendActionResult</c> partial describing the outcome.</returns>
    [HttpPost("requests")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Send([FromForm] SendFriendRequestForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        var result = await sender.Send(new SendFriendRequestCommand(form.AddresseeUserId), cancellationToken);
        return PartialView("_FriendActionResult", new FriendActionResultVm(MessageFor(result.Outcome)));
    }

    /// <summary>
    /// Accepts an incoming friend request (<c>POST /friends/requests/{id}/accept</c>). Only the addressee may
    /// respond (403 otherwise) — enforced in the handler. Returns a status partial the request row swaps in.
    /// </summary>
    /// <param name="id">The <c>Friend</c> request id (from the route).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The <c>_FriendActionResult</c> partial.</returns>
    [HttpPost("requests/{id:guid}/accept")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Accept(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new RespondToFriendRequestCommand(id, Accept: true), cancellationToken);
        return PartialView("_FriendActionResult", new FriendActionResultVm("You are now friends."));
    }

    /// <summary>
    /// Declines an incoming friend request (<c>POST /friends/requests/{id}/decline</c>). Only the addressee may
    /// respond (403 otherwise). Returns a status partial the request row swaps in.
    /// </summary>
    /// <param name="id">The <c>Friend</c> request id (from the route).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The <c>_FriendActionResult</c> partial.</returns>
    [HttpPost("requests/{id:guid}/decline")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Decline(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new RespondToFriendRequestCommand(id, Accept: false), cancellationToken);
        return PartialView("_FriendActionResult", new FriendActionResultVm("Request declined."));
    }

    /// <summary>
    /// Cancels the current user's outgoing pending request (<c>POST /friends/requests/{id}/cancel</c>).
    /// Requester-only (403 otherwise, enforced in the handler). Returns a status partial the request row swaps in.
    /// </summary>
    /// <param name="id">The <c>Friend</c> request id (from the route).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The <c>_FriendActionResult</c> partial.</returns>
    [HttpPost("requests/{id:guid}/cancel")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new CancelFriendRequestCommand(id), cancellationToken);
        return PartialView("_FriendActionResult", new FriendActionResultVm("Request cancelled."));
    }

    /// <summary>
    /// Removes an accepted friend (<c>DELETE /friends/{otherUserId}</c>). The endpoint serves two swap contexts,
    /// distinguished by HTMX's <c>HX-Target</c> header. On the PROFILE page the request targets the whole
    /// <c>#profile-friend-control</c> (<c>hx-swap="outerHTML"</c>), so an empty body would erase the control
    /// entirely (no re-add affordance, focus dropped) — instead re-render <c>_ProfileFriendControl</c> in its
    /// post-removal state (the relationship is now <see cref="ProfileRelationship.None"/> → the "Add friend"
    /// form). On the friends LIST the card SHOULD disappear, so the empty <c>200</c> stays. Idempotent —
    /// removing a non-friend is a no-op inside the handler.
    /// </summary>
    /// <param name="otherUserId">The id of the friend to remove (from the route).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The re-rendered <c>_ProfileFriendControl</c> partial (profile context) or an empty <c>200</c> (list context).</returns>
    [HttpDelete("{otherUserId:guid}")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Remove(Guid otherUserId, CancellationToken cancellationToken)
    {
        await sender.Send(new RemoveFriendCommand(otherUserId), cancellationToken);

        if (IsProfileFriendControlTarget())
        {
            // Re-resolve the profile so the control re-renders honestly (relationship is now None → "Add
            // friend"); GetProfileQuery re-checks the relationship rather than fabricating a None state.
            var profile = await sender.Send(new GetProfileQuery(otherUserId), cancellationToken);
            return PartialView("_ProfileFriendControl", profile);
        }

        return Ok();
    }

    /// <summary>
    /// Blocks a user (<c>POST /friends/{userId}/block</c>) — full cut-off + hide (§2). On the profile page the
    /// control re-renders in its <see cref="ProfileRelationship.BlockedByMe"/> (Unblock) state; on a list the
    /// row is removed (empty 200). Silent and idempotent (handler).
    /// </summary>
    /// <param name="userId">The id of the user to block (from the route).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The re-rendered <c>_ProfileFriendControl</c> (profile) or an empty <c>200</c> (list).</returns>
    [HttpPost("{userId:guid}/block")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Block(Guid userId, CancellationToken cancellationToken)
    {
        await sender.Send(new BlockUserCommand(userId), cancellationToken);

        if (IsProfileFriendControlTarget())
        {
            var profile = await sender.Send(new GetProfileQuery(userId), cancellationToken);
            return PartialView("_ProfileFriendControl", profile);
        }

        return Ok();
    }

    /// <summary>
    /// Unblocks a user (<c>DELETE /friends/{userId}/block</c>). On the profile page the control re-renders in its
    /// post-unblock state (now <see cref="ProfileRelationship.None"/> → "Add friend"); on the Blocked tab the row
    /// is removed (empty 200). Idempotent.
    /// </summary>
    /// <param name="userId">The id of the user to unblock (from the route).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The re-rendered <c>_ProfileFriendControl</c> (profile) or an empty <c>200</c> (list).</returns>
    [HttpDelete("{userId:guid}/block")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Unblock(Guid userId, CancellationToken cancellationToken)
    {
        await sender.Send(new UnblockUserCommand(userId), cancellationToken);

        if (IsProfileFriendControlTarget())
        {
            var profile = await sender.Send(new GetProfileQuery(userId), cancellationToken);
            return PartialView("_ProfileFriendControl", profile);
        }

        return Ok();
    }

    // HTMX sends the resolved target element's id (no '#') in the HX-Target header; the profile control's
    // wrapper id identifies the profile-page swap context so Remove can re-render it instead of blanking it.
    private bool IsProfileFriendControlTarget() =>
        string.Equals(Request.Headers["HX-Target"].ToString(), "profile-friend-control", StringComparison.Ordinal);

    // Maps a directed-pair guard outcome to a short, user-facing status line.
    private static string MessageFor(FriendRequestOutcome outcome) => outcome switch
    {
        FriendRequestOutcome.Created => "Friend request sent.",
        FriendRequestOutcome.AlreadyFriends => "You are already friends.",
        FriendRequestOutcome.AlreadyRequested => "Friend request already sent.",
        FriendRequestOutcome.ReciprocalPending => "They already sent you a request — respond in your requests.",
        FriendRequestOutcome.Blocked => "Friend request sent.",
        _ => "Friend request sent.",
    };
}
