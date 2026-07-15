using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Profiles;
using Cinora.Web.ViewModels.Common;
using Microsoft.AspNetCore.Mvc;

namespace Cinora.Web.Components;

/// <summary>
/// Renders the signed-in user's nav avatar chip (Milestone 4.2 Step B). Invoked from <c>_Layout.cshtml</c> inside
/// the authenticated branch only, it dispatches <see cref="GetMyProfileSettingsQuery"/> — a single
/// <c>AsNoTracking</c> projection of the current user's row by primary key (the same owner-only read the settings
/// page uses, resolving the actor server-side via <c>ICurrentUser</c>) — and renders the shared <c>_Avatar</c>
/// seam with the resolved display name + avatar key. The layout renders once per full-page request, so this is one
/// indexed PK lookup per page (HTMX partial responses skip the layout entirely). A read failure degrades to the
/// monogram built from the auth cookie's name rather than breaking the whole layout — the underlying error is
/// already logged by the mediator's logging behavior. Mirrors the <see cref="NotificationBellViewComponent"/>
/// precedent.
/// </summary>
/// <param name="sender">The hand-rolled mediator used to dispatch the settings query.</param>
public sealed class CurrentUserAvatarViewComponent(ISender sender) : ViewComponent
{
    /// <summary>Resolves the current user's display name + avatar key and renders the <c>_Avatar</c> seam.</summary>
    /// <returns>The <c>Default</c> view bound to an <see cref="AvatarViewModel"/>.</returns>
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var cancellationToken = HttpContext.RequestAborted;

        // The auth cookie's name is the graceful fallback if the projection fails (the chip still renders a monogram).
        var fallbackName = User.Identity?.Name ?? "?";

        AvatarViewModel model;
        try
        {
            var settings = await sender.Send(new GetMyProfileSettingsQuery(), cancellationToken);
            // Eager: the nav chip is above the fold on every authed page (Milestone 6.4 §6.5, backlog 4.2).
            model = new AvatarViewModel(settings.DisplayName, settings.AvatarFileKey, AvatarSize.Small, Eager: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Degrade gracefully: a non-critical nav chip must not take the page down. The mediator's logging
            // behavior already recorded the failure, so no second log here.
            model = new AvatarViewModel(fallbackName, AvatarFileKey: null, AvatarSize.Small);
        }

        return View(model);
    }
}
