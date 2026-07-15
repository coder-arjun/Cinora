using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Profiles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cinora.Web.Controllers;

/// <summary>
/// The authenticated profile surface (Milestone 3.3): a user's privacy-shaped profile, addressed by
/// <c>Guid</c> (there is no username/handle column — §15). The class is <see cref="AuthorizeAttribute"/>
/// (fail-closed): an anonymous request is redirected to login by the auth pipeline (Phase 3 requires
/// authentication for all profiles — §7.3). The controller stays thin: it dispatches
/// <see cref="GetProfileQuery"/> — which shapes visibility by relationship at the data read — and returns the
/// view; a missing user 404s inside the handler.
/// </summary>
/// <param name="sender">The hand-rolled mediator used to dispatch the profile query.</param>
[Authorize]
[Route("users")]
public sealed class ProfilesController(ISender sender) : Controller
{
    /// <summary>
    /// A user's profile (<c>GET /users/{id}</c>), privacy-shaped for the current viewer: full for the owner or
    /// an accepted friend, public (reviews) for a signed-in non-friend of a public profile, or limited (name +
    /// avatar only) for a private one.
    /// </summary>
    /// <param name="id">The profile owner's user id (from the route).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handler.</param>
    /// <returns>The <c>Profile</c> view; <c>404</c> when no such user exists.</returns>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Profile(Guid id, CancellationToken cancellationToken)
    {
        var profile = await sender.Send(new GetProfileQuery(id), cancellationToken);
        return View(profile);
    }
}
