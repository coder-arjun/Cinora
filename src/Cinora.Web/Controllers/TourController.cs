using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cinora.Web.Controllers;

/// <summary>
/// Serves the public, fullscreen product walkthrough reached from the landing page's "Explore Cinora" button.
/// A static, anonymous marketing surface with no data access — the slideshow interactivity lives entirely in the
/// CSP-safe <c>tour</c> Alpine component (<c>Scripts/components/tour.ts</c>). The global fail-closed authorization
/// fallback means the anonymous entry point must be an explicit <see cref="AllowAnonymousAttribute"/> opt-out.
/// </summary>
[AllowAnonymous]
[Route("tour")]
public sealed class TourController : Controller
{
    /// <summary>Renders the fullscreen feature walkthrough.</summary>
    /// <returns>The tour view.</returns>
    [HttpGet("")]
    public IActionResult Index() => View();
}
