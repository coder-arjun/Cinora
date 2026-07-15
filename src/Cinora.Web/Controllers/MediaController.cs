using Cinora.Infrastructure.Storage;
using Cinora.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;

namespace Cinora.Web.Controllers;

/// <summary>
/// Serves user-uploaded avatars from the local upload store through a token-gated route (Milestone 4.1,
/// ADR 0013 §2.4) — the free stand-in for a Blob SAS. It is <see cref="AllowAnonymousAttribute"/> because the
/// signed <c>t</c> token IS the capability (like a SAS), not the auth cookie: public title-detail pages render
/// reviewer avatars anonymously. The route is same-origin (<c>/uploads/avatar…</c>), so the existing strict CSP
/// (<c>img-src 'self' …</c>) already covers it with NO widening. Controllers stay thin: all token verification
/// and filesystem access live behind <see cref="IAvatarContentSource"/> in Infrastructure.
/// </summary>
/// <remarks>
/// The action never trusts or echoes a client content-type or path: it forces the file's TRUE content-type
/// (derived from the magic-byte-confirmed extension), adds <c>X-Content-Type-Options: nosniff</c>, serves
/// <c>Content-Disposition: inline</c> with a private cache window, and attaches a cheap weak validator ETag
/// (length + last-write-time) so conditional GETs short-circuit to 304 BEFORE the file is read (Milestone 6.4
/// §6.3). The bytes stream via <c>PhysicalFileResult</c> from <c>App_Data/uploads</c> — never static-served. An
/// invalid, tampered, or expired token — or a missing file — is a <c>404</c>, never a filesystem escape. GET-only,
/// so it is exempt from the global anti-forgery validation.
/// </remarks>
/// <param name="avatars">The Infrastructure serving seam that verifies the token and reads the bytes.</param>
[AllowAnonymous]
[EnableRateLimiting(RateLimitingPolicies.PublicRead)]
// SERVE side of the mint↔serve coupling: this MUST stay the base segment the minted URL targets. The mint side
// (LocalFileStorage.GetUrl, via FileStorageOptions.PublicBasePath) uses the SAME AvatarServingRoute const, and
// FileStorageRouteValidateOptions fails boot if PublicBasePath ever drifts from it — so the two cannot silently
// diverge into an all-avatars-404 (ADR 0013 §2.4).
[Route(AvatarServingRoute.RouteBase)]
public sealed class MediaController(IAvatarContentSource avatars) : Controller
{
    // Browsers may cache the bytes privately for the day; the bucketed-expiry URL rotates each bucket.
    private const int CacheMaxAgeSeconds = 86_400;

    /// <summary>
    /// <c>GET /uploads/avatar?t={token}</c> — verifies the signed token and streams the referenced avatar with
    /// its forced true content-type and a cheap weak validator ETag, or returns <c>404</c> for any
    /// invalid/expired/tampered token or missing file.
    /// </summary>
    /// <param name="t">The opaque, signed, time-limited serving token minted by <c>IFileStorage.GetUrl</c>.</param>
    /// <returns>The avatar bytes (200/304 for conditional GETs), or <c>404</c> when it cannot be served.</returns>
    [HttpGet("avatar")]
    public IActionResult Avatar([FromQuery(Name = "t")] string? t)
    {
        var file = avatars.TryResolve(t);
        if (file is null)
        {
            // Covers a missing/garbage/tampered/expired token AND a missing file — never leak which.
            return NotFound();
        }

        // Forced true type + nosniff + inline + private cache. nosniff is also set globally by
        // SecurityHeadersMiddleware; setting it here makes the controller's security posture self-contained.
        Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        Response.Headers[HeaderNames.ContentDisposition] = "inline";
        Response.Headers[HeaderNames.CacheControl] = $"private, max-age={CacheMaxAgeSeconds}";

        // Milestone 6.4 (§6.3): stream the file via PhysicalFileResult and pass the WEAK validator so the
        // framework answers a matching If-None-Match with 304 BEFORE opening the file — no per-GET read+rehash.
        // The bytes are served from App_Data/uploads through the controller — never static-served from wwwroot.
        return PhysicalFile(
            file.PhysicalPath,
            file.ContentType,
            lastModified: null,
            entityTag: EntityTagHeaderValue.Parse(file.ETag));
    }
}
