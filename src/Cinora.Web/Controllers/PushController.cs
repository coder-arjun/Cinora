using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Push;
using Cinora.Infrastructure.Options;
using Cinora.Web.Infrastructure;
using Cinora.Web.ViewModels.Push;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Cinora.Web.Controllers;

/// <summary>
/// The Web Push subscription endpoints (Milestone 6.2, ADR 0020 §3.1). The class is
/// <see cref="AuthorizeAttribute"/> (fail-closed) and binds <b>no</b> user id — the subscribing user is
/// resolved server-side inside the handlers via <c>ICurrentUser</c> (ADR 0009), so there is no cross-user
/// subscribe/unsubscribe path. The write POSTs carry the anti-forgery token via the global
/// <c>AutoValidateAntiforgeryToken</c> (the same-origin <c>fetch</c> sends it in the <c>RequestVerificationToken</c>
/// header) and a per-user <c>social-write</c> rate limit. All three actions are same-origin
/// (<c>connect-src 'self'</c>) — the strict CSP is untouched (§4). The controller stays thin: it model-binds,
/// dispatches via <see cref="ISender"/>, and returns a result.
/// </summary>
/// <param name="sender">The hand-rolled mediator used to dispatch the device commands.</param>
/// <param name="webPushOptions">Supplies the non-secret VAPID public key served to the client (or unconfigured).</param>
[Authorize]
[Route("push")]
public sealed class PushController(ISender sender, IOptions<WebPushOptions> webPushOptions) : Controller
{
    /// <summary>
    /// Registers (upserts) the current user's push subscription (<c>POST /push/subscribe</c>). The browser
    /// <c>PushSubscription</c> JSON is unwrapped into <see cref="RegisterDeviceCommand"/>; a partial/empty body is
    /// a friendly 400. Returns <c>204</c> on success.
    /// </summary>
    /// <param name="subscription">The browser subscription body (<c>{ endpoint, keys: { p256dh, auth } }</c>).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handler.</param>
    /// <returns><c>204 No Content</c> on success; <c>400</c> when the subscription is incomplete.</returns>
    [HttpPost("subscribe")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Subscribe(
        [FromBody] BrowserPushSubscription? subscription, CancellationToken cancellationToken)
    {
        if (subscription is null
            || string.IsNullOrWhiteSpace(subscription.Endpoint)
            || subscription.Keys is null
            || string.IsNullOrWhiteSpace(subscription.Keys.P256dh)
            || string.IsNullOrWhiteSpace(subscription.Keys.Auth))
        {
            ModelState.AddModelError("subscription", "A complete push subscription (endpoint and keys) is required.");
            return ValidationProblem(ModelState);
        }

        await sender.Send(
            new RegisterDeviceCommand(subscription.Endpoint, subscription.Keys.P256dh, subscription.Keys.Auth),
            cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Removes the current user's push subscription (<c>POST /push/unsubscribe</c>) by endpoint. Idempotent
    /// (no matching device is still a <c>204</c>). A missing endpoint is a friendly 400.
    /// </summary>
    /// <param name="request">The unsubscribe body (<c>{ endpoint }</c>).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handler.</param>
    /// <returns><c>204 No Content</c>; <c>400</c> when no endpoint was supplied.</returns>
    [HttpPost("unsubscribe")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Unsubscribe(
        [FromBody] PushUnsubscribeRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Endpoint))
        {
            ModelState.AddModelError("endpoint", "A push endpoint is required.");
            return ValidationProblem(ModelState);
        }

        await sender.Send(new UnregisterDeviceCommand(request.Endpoint), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Returns the non-secret VAPID public key the client passes as <c>applicationServerKey</c> when subscribing
    /// (<c>GET /push/public-key</c>). When push is <b>unconfigured</b> (no VAPID keys) it returns <c>404</c> so the
    /// subscribe UI can stay hidden — push is additive and degrades to off, it never crashes (§8).
    /// </summary>
    /// <returns>The VAPID public key as <c>text/plain</c>, or <c>404</c> when push is not configured.</returns>
    [HttpGet("public-key")]
    public IActionResult PublicKey()
    {
        var options = webPushOptions.Value;
        return options.IsConfigured && !string.IsNullOrWhiteSpace(options.PublicKey)
            ? Content(options.PublicKey, "text/plain")
            : NotFound();
    }

    /// <summary>
    /// Sends a real TEST push to the current user's own devices (<c>POST /push/test</c>) so they can confirm
    /// notifications work on this device (the opt-in bar's "Send test"). Returns a friendly JSON <c>{ message }</c>:
    /// unconfigured server, no subscribed device yet, or delivered-to-N. Anti-forgery + the per-user social-write
    /// limit apply (same as the other write POSTs).
    /// </summary>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handler.</param>
    /// <returns><c>200</c> with a friendly <c>{ message }</c> describing the outcome.</returns>
    [HttpPost("test")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Test(CancellationToken cancellationToken)
    {
        if (!webPushOptions.Value.IsConfigured)
        {
            return Ok(new { message = "Push isn't configured on the server yet." });
        }

        var result = await sender.Send(new SendTestPushCommand(), cancellationToken);
        var message = result.DeviceCount == 0
            ? "No subscribed device yet — tap Enable first."
            : $"Test notification sent to {result.DeviceCount} device(s).";
        return Ok(new { message });
    }
}
