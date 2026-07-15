using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Notifications;
using Microsoft.AspNetCore.Mvc;

namespace Cinora.Web.Components;

/// <summary>
/// Renders the nav notification bell + live unread badge for an authenticated user (Milestone 3.5). It is
/// invoked from <c>_Layout.cshtml</c> (only inside the authenticated branch), dispatches
/// <see cref="GetUnreadCountQuery"/> for the initial badge count, and renders a bell linking to
/// <c>/notifications</c>. The badge carries a stable id/marker the frontend's SignalR client updates live on
/// <c>UnreadCountChanged</c>; this component only seeds the initial server-rendered count (the JS client is a
/// separate frontend task). A badge read failure degrades to a zero count rather than breaking the whole layout
/// — the underlying error is already logged by the mediator's logging behavior.
/// </summary>
/// <param name="sender">The hand-rolled mediator used to dispatch the unread-count query.</param>
public sealed class NotificationBellViewComponent(ISender sender) : ViewComponent
{
    /// <summary>Resolves the current user's unread count and renders the bell + badge.</summary>
    /// <returns>The <c>Default</c> view bound to the unread count.</returns>
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var cancellationToken = HttpContext.RequestAborted;

        int unreadCount;
        try
        {
            unreadCount = await sender.Send(new GetUnreadCountQuery(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Degrade gracefully: a non-critical badge must not take the page down. The mediator's logging
            // behavior already recorded the failure, so no second log here.
            unreadCount = 0;
        }

        return View(unreadCount);
    }
}
