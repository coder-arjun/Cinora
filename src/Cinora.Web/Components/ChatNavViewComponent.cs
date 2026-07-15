using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Chat;
using Microsoft.AspNetCore.Mvc;

namespace Cinora.Web.Components;

/// <summary>
/// Renders the nav chat entry + unread badge for an authenticated user (§9). Invoked from <c>_Layout.cshtml</c>
/// inside the authenticated branch; it dispatches <see cref="GetChatUnreadCountQuery"/> for the initial badge
/// count and renders a chat link to <c>/chat</c>. The badge carries a stable id (<c>chat-unread-badge</c>) so a
/// future live update can target it; this component seeds the server-rendered count (accurate on every
/// navigation). A read failure degrades to zero rather than breaking the layout — the mediator's logging behavior
/// already recorded the error.
/// </summary>
/// <param name="sender">The hand-rolled mediator used to dispatch the unread-count query.</param>
public sealed class ChatNavViewComponent(ISender sender) : ViewComponent
{
    /// <summary>Resolves the current user's chat unread count and renders the chat link + badge.</summary>
    /// <returns>The <c>Default</c> view bound to the unread count.</returns>
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var cancellationToken = HttpContext.RequestAborted;

        int unreadCount;
        try
        {
            unreadCount = await sender.Send(new GetChatUnreadCountQuery(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            unreadCount = 0;
        }

        return View(unreadCount);
    }
}
