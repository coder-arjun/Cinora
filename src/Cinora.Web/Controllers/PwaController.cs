using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cinora.Web.Controllers;

/// <summary>
/// Serves the PWA offline fallback page (Milestone 6.1, ADR 0019). The service worker precaches
/// <c>/offline</c> on install and serves it for an uncached navigation while the device is offline. The page
/// carries NO personalized data, so it is anonymously reachable (the SW must load it pre-auth) and safe to
/// store in the shared service-worker cache — it deliberately does <b>not</b> emit the <c>no-store</c> header
/// the authenticated pages do.
/// </summary>
[AllowAnonymous]
public sealed class PwaController : Controller
{
    /// <summary>The branded dark-theme offline fallback (<c>GET /offline</c>).</summary>
    /// <returns>The offline view.</returns>
    [HttpGet("/offline")]
    public IActionResult Offline() => View();
}
