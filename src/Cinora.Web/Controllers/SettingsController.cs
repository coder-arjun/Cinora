using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Notifications;
using Cinora.Application.Features.Profiles;
using Cinora.Infrastructure.Options;
using Cinora.Web.Infrastructure;
using Cinora.Web.ViewModels.Settings;
using Cinora.Web.ViewModels.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Cinora.Web.Controllers;

/// <summary>
/// The owner-only account settings surface (Milestone 4.2, §4.2): edit the current user's display name, toggle
/// privacy (public / friends-only), and upload / replace / remove the avatar. The class is
/// <see cref="AuthorizeAttribute"/> (fail-closed) and binds NO user id — every action operates on
/// <c>ICurrentUser</c>, so there is no cross-user edit path (ADR 0009). It stays thin: it model-binds, dispatches
/// via <see cref="ISender"/>, and returns an HTMX partial or a redirect. Every state-changing verb carries the
/// anti-forgery token via the global <c>AutoValidateAntiforgeryToken</c> (the multipart avatar POST carries it
/// via the <c>RequestVerificationToken</c> header / hidden field — no opt-out, no new wiring). Avatars are
/// served same-origin, so the strict CSP is untouched (§3).
/// </summary>
/// <param name="sender">The hand-rolled mediator used to dispatch the profile commands/queries.</param>
/// <param name="fileStorageOptions">Supplies the client-side upload size hint only; the server stays authoritative.</param>
[Authorize]
[Route("settings")]
public sealed class SettingsController(ISender sender, IOptions<FileStorageOptions> fileStorageOptions) : Controller
{
    // A coarse request-body backstop above the default MaxUploadBytes (5 MB) plus multipart framing headroom.
    // It is belt-and-suspenders under the validator/adapter, which enforce the precise per-file cap from options;
    // an oversize body is rejected before buffering (a DoS guard), while the validator gives the friendly 400.
    //
    // COUPLING (fail-fast enforced at boot): this coarse backstop must never be TIGHTER than the precise per-file
    // cap FileStorageOptions.MaxUploadBytes — otherwise an operator who raises MaxUploadBytes above this const
    // would have valid uploads 413 at the [RequestSizeLimit] guard BEFORE the streaming cap runs (silent config
    // drift). The const lives here in Web because [RequestSizeLimit] needs a compile-time constant, while the
    // option lives in Infrastructure; Program.cs (the composition root, which sees both) asserts
    // MaxUploadBytes <= this const at startup. Keep comfortable framing headroom above MaxUploadBytes (~25%), and
    // it must stay internal so that boot guard can read it.
    internal const long AvatarRequestSizeLimitBytes = 6_291_456; // 6 MiB

    /// <summary>
    /// The settings edit page (<c>GET /settings/profile</c>): the display-name field, the current avatar (via the
    /// <c>_Avatar</c> seam) with an upload/replace/remove control, and the public / friends-only privacy toggle.
    /// Pre-filled from <see cref="GetMyProfileSettingsQuery"/>.
    /// </summary>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handler.</param>
    /// <returns>The <c>EditProfile</c> view.</returns>
    [HttpGet("profile")]
    public async Task<IActionResult> EditProfile(CancellationToken cancellationToken)
    {
        var settings = await sender.Send(new GetMyProfileSettingsQuery(), cancellationToken);
        return View("EditProfile", ToViewModel(settings, saved: false));
    }

    /// <summary>
    /// Applies a display-name + privacy edit (<c>POST /settings/profile</c>). Under HTMX it returns the
    /// re-rendered <c>_ProfileForm</c> fragment (with a saved confirmation); a no-JS post redirects back to the
    /// edit page.
    /// </summary>
    /// <param name="form">The bound edit form (display name + privacy; no user id).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handlers.</param>
    /// <returns>The <c>_ProfileForm</c> partial (HTMX) or a redirect to the edit page (no-JS).</returns>
    [HttpPost("profile")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> UpdateProfile(
        [FromForm] UpdateProfileForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        await sender.Send(
            new UpdateProfileCommand(form.DisplayName, form.IsProfilePublic, LanguageOptions.Normalize(form.DefaultLanguage)),
            cancellationToken);

        if (!IsHtmxRequest())
        {
            return RedirectToAction(nameof(EditProfile));
        }

        var settings = await sender.Send(new GetMyProfileSettingsQuery(), cancellationToken);
        return PartialView("_ProfileForm", ToViewModel(settings, saved: true));
    }

    /// <summary>
    /// Uploads / replaces the avatar (<c>POST /settings/profile/avatar</c>, <c>multipart/form-data</c>). The
    /// <c>IFormFile</c> is unwrapped to a stream + content-type + length and dispatched as
    /// <see cref="ChangeAvatarCommand"/> (the Web type never crosses into Application). Returns the re-rendered
    /// <c>_Avatar</c> fragment (the new avatar) to swap. A missing/empty file is a friendly 400.
    /// </summary>
    /// <param name="file">The uploaded image (the only multipart field the action reads).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handlers.</param>
    /// <returns>The <c>_Avatar</c> partial (new avatar); <c>400</c> when no file was supplied.</returns>
    [HttpPost("profile/avatar")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    [RequestSizeLimit(AvatarRequestSizeLimitBytes)]
    public async Task<IActionResult> UploadAvatar(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            // Return a consistent RFC-7807 ValidationProblemDetails (400) rather than a plain string body, matching
            // the app's error shape everywhere else and the 400 the ChangeAvatarCommand validator would itself
            // produce for an empty upload. No sensitive detail is exposed.
            ModelState.AddModelError("file", "Please choose an image to upload.");
            return ValidationProblem(ModelState);
        }

        await using (var content = file.OpenReadStream())
        {
            await sender.Send(
                new ChangeAvatarCommand(content, file.ContentType, file.Length), cancellationToken);
        }

        var settings = await sender.Send(new GetMyProfileSettingsQuery(), cancellationToken);
        return PartialView("_Avatar", ToAvatar(settings));
    }

    /// <summary>
    /// Removes the avatar (<c>DELETE /settings/profile/avatar</c>) and returns the monogram <c>_Avatar</c>
    /// fragment. Idempotent — removing when there is no avatar is a no-op inside <see cref="RemoveAvatarCommand"/>.
    /// </summary>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handlers.</param>
    /// <returns>The <c>_Avatar</c> partial (monogram fallback).</returns>
    [HttpDelete("profile/avatar")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> RemoveAvatar(CancellationToken cancellationToken)
    {
        await sender.Send(new RemoveAvatarCommand(), cancellationToken);

        var settings = await sender.Send(new GetMyProfileSettingsQuery(), cancellationToken);
        return PartialView("_Avatar", ToAvatar(settings));
    }

    /// <summary>
    /// The notification-settings page (<c>GET /settings/notifications</c>, Milestone 6.3): the four per-type push
    /// toggles pre-filled from <see cref="GetNotificationSettingsQuery"/>, plus the "Enable push on this device"
    /// subscribe control. This is the one canonical notification-settings surface (it replaces the Phase-4
    /// placeholder + the 6.2 subscribe control that previously lived on the profile page).
    /// </summary>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handler.</param>
    /// <returns>The <c>Notifications</c> view.</returns>
    [HttpGet("notifications")]
    public async Task<IActionResult> Notifications(CancellationToken cancellationToken)
    {
        var settings = await sender.Send(new GetNotificationSettingsQuery(), cancellationToken);
        return View("Notifications", ToViewModel(settings, saved: false));
    }

    /// <summary>
    /// Applies a per-type push-preference edit (<c>POST /settings/notifications</c>). Owner-only — the command
    /// binds no user id (ADR 0009). Under HTMX it returns the re-rendered <c>_NotificationPreferencesForm</c>
    /// fragment (with a saved confirmation); a no-JS post redirects back to the notifications page.
    /// </summary>
    /// <param name="form">The bound toggles form (the four push flags; no user id).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>; threaded to the handlers.</param>
    /// <returns>The <c>_NotificationPreferencesForm</c> partial (HTMX) or a redirect (no-JS).</returns>
    [HttpPost("notifications")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> UpdateNotifications(
        [FromForm] UpdateNotificationPreferencesForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        await sender.Send(
            new UpdateNotificationPreferencesCommand(
                form.PushFriendRequests, form.PushFriendAccepted, form.PushReviewLikes, form.PushComments),
            cancellationToken);

        if (!IsHtmxRequest())
        {
            return RedirectToAction(nameof(Notifications));
        }

        var settings = await sender.Send(new GetNotificationSettingsQuery(), cancellationToken);
        return PartialView("_NotificationPreferencesForm", ToViewModel(settings, saved: true));
    }

    private ProfileSettingsViewModel ToViewModel(MyProfileSettingsVm settings, bool saved) =>
        new()
        {
            DisplayName = settings.DisplayName,
            IsProfilePublic = settings.IsProfilePublic,
            AvatarFileKey = settings.AvatarFileKey,
            DefaultLanguage = settings.DefaultLanguage,
            MaxUploadBytes = fileStorageOptions.Value.MaxUploadBytes,
            Saved = saved,
        };

    private static AvatarViewModel ToAvatar(MyProfileSettingsVm settings) =>
        new(settings.DisplayName, settings.AvatarFileKey, AvatarSize.Large);

    private static NotificationSettingsViewModel ToViewModel(NotificationSettingsVm settings, bool saved) =>
        new()
        {
            PushFriendRequests = settings.PushFriendRequests,
            PushFriendAccepted = settings.PushFriendAccepted,
            PushReviewLikes = settings.PushReviewLikes,
            PushComments = settings.PushComments,
            Saved = saved,
        };

    // HTMX sets the "HX-Request" header on every AJAX request; its absence means a plain (no-JS) form post.
    private bool IsHtmxRequest() => Request.Headers.ContainsKey("HX-Request");
}
