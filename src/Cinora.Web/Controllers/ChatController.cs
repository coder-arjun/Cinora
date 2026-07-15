using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Chat;
using Cinora.Application.Features.Friends;
using Cinora.Domain.Enums;
using Cinora.Web.Infrastructure;
using Cinora.Web.ViewModels.Chat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cinora.Web.Controllers;

/// <summary>
/// The authenticated chat surface (§6): the two-pane <c>/chat</c> page (conversation list + open thread), sending
/// and paging messages, starting DMs, and creating/managing groups. The controller stays thin — it model-binds,
/// dispatches via <see cref="ISender"/>, and returns a view/partial or a redirect. Every access/ownership rule
/// (participation, friends-only, admin-only, sender-only) lives in the handlers (§3, ADR 0009); the acting user is
/// always server-resolved, never bound. Writes are anti-forgery-validated (the global filter) and rate-limited
/// with the shared per-user <see cref="RateLimitingPolicies.SocialWrite"/> policy.
/// </summary>
/// <param name="sender">The hand-rolled mediator used to dispatch the chat commands/queries.</param>
[Authorize]
[Route("chat")]
public sealed class ChatController(ISender sender) : Controller
{
    /// <summary>The chat home (<c>GET /chat</c>): the conversation list with an empty thread pane.</summary>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The two-pane <c>Index</c> view with no thread open.</returns>
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var conversations = await sender.Send(new GetConversationsQuery(), cancellationToken);
        var friends = await sender.Send(new GetFriendsQuery(), cancellationToken);
        return View(new ChatPageVm(conversations.Conversations, ActiveThread: null, friends.Friends));
    }

    /// <summary>
    /// A conversation open in the thread pane (<c>GET /chat/{id}</c>): the list plus that conversation's header
    /// and newest message page. Membership is enforced in the handlers (a non-member → 403, unknown → 404).
    /// </summary>
    /// <param name="id">The conversation to open.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The two-pane <c>Index</c> view with the thread open.</returns>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Thread(Guid id, CancellationToken cancellationToken)
    {
        var conversations = await sender.Send(new GetConversationsQuery(), cancellationToken);
        var friends = await sender.Send(new GetFriendsQuery(), cancellationToken);
        var header = await sender.Send(new GetConversationHeaderQuery(id), cancellationToken);
        var messages = await sender.Send(new GetMessagesQuery(id, null, null), cancellationToken);
        return View("Index", new ChatPageVm(
            conversations.Conversations, new ChatThreadVm(header, messages), friends.Friends));
    }

    /// <summary>
    /// An older keyset page of a conversation's history (<c>GET /chat/{id}/messages?cursorSentAt=&amp;cursorId=</c>)
    /// for load-more. Returns just the message-list partial.
    /// </summary>
    /// <param name="id">The conversation whose history to page.</param>
    /// <param name="cursorSentAt">The previous page's last message instant.</param>
    /// <param name="cursorId">The previous page's last message id (the tie-break).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The <c>_MessageList</c> partial for the older page.</returns>
    [HttpGet("{id:guid}/messages")]
    public async Task<IActionResult> History(
        Guid id, [FromQuery] DateTime? cursorSentAt, [FromQuery] Guid? cursorId, CancellationToken cancellationToken)
    {
        var messages = await sender.Send(new GetMessagesQuery(id, cursorSentAt, cursorId), cancellationToken);
        return PartialView("_MessageList", messages);
    }

    /// <summary>
    /// Sends a message (<c>POST /chat/{id}/messages</c>) and returns the rendered bubble the composer appends.
    /// The realtime echo to the sender's own connection is deduped client-side by message id.
    /// </summary>
    /// <param name="id">The conversation to post to.</param>
    /// <param name="form">The bound composer form (the body).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The <c>_ChatMessage</c> partial for the just-sent message.</returns>
    [HttpPost("{id:guid}/messages")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Send(Guid id, [FromForm] SendMessageForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        var message = await sender.Send(new SendMessageCommand(id, form.Body), cancellationToken);
        return PartialView("_ChatMessage", message);
    }

    /// <summary>
    /// The movie-share picker (<c>GET /chat/share</c>): renders the title being shared plus the destinations —
    /// the user's friends (each opens/creates a DM) and their groups. Reached from a title's "Send to a friend"
    /// button.
    /// </summary>
    /// <param name="tmdbId">The shared title's TMDB id.</param>
    /// <param name="mediaType">The media-type token (e.g. "Movie"/"Series").</param>
    /// <param name="title">The shared title's display name.</param>
    /// <param name="posterPath">The shared title's raw TMDB poster path, or <c>null</c>.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The <c>Share</c> page.</returns>
    [HttpGet("share")]
    public async Task<IActionResult> Share(
        [FromQuery] int tmdbId,
        [FromQuery] string mediaType,
        [FromQuery] string title,
        [FromQuery] string? posterPath,
        CancellationToken cancellationToken)
    {
        var friends = await sender.Send(new GetFriendsQuery(), cancellationToken);
        var conversations = await sender.Send(new GetConversationsQuery(), cancellationToken);
        var groups = conversations.Conversations.Where(c => c.Type == ConversationType.Group).ToList();
        return View(new SharePickerVm(tmdbId, mediaType ?? string.Empty, title ?? string.Empty, posterPath, friends.Friends, groups));
    }

    /// <summary>Shares a movie into an existing conversation (<c>POST /chat/{id}/share-movie</c>) and opens it.</summary>
    /// <param name="id">The conversation to share into.</param>
    /// <param name="form">The bound share form (movie coordinates + optional caption).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>A redirect to the conversation thread.</returns>
    [HttpPost("{id:guid}/share-movie")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> ShareMovieToConversation(
        Guid id, [FromForm] ShareMovieForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        await sender.Send(
            new ShareMovieToConversationCommand(id, form.TmdbId, form.MediaType, form.Title, form.PosterPath, form.Caption),
            cancellationToken);
        return RedirectToAction(nameof(Thread), new { id });
    }

    /// <summary>
    /// Shares a movie with a friend (<c>POST /chat/share-movie/friend</c>): find-or-create the DM, then share, and
    /// open the thread. The friends-only + blocking gate lives in the handlers.
    /// </summary>
    /// <param name="form">The bound share form (the friend id + movie coordinates + optional caption).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>A redirect to the direct conversation thread.</returns>
    [HttpPost("share-movie/friend")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> ShareMovieToFriend([FromForm] ShareMovieForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        var conversationId = await sender.Send(new StartDirectConversationCommand(form.OtherUserId), cancellationToken);
        await sender.Send(
            new ShareMovieToConversationCommand(conversationId, form.TmdbId, form.MediaType, form.Title, form.PosterPath, form.Caption),
            cancellationToken);
        return RedirectToAction(nameof(Thread), new { id = conversationId });
    }

    /// <summary>
    /// Starts or opens a direct chat with a friend (<c>POST /chat/direct</c>) and redirects to the thread. The
    /// friends-only + blocking gate and find-or-create are in the handler.
    /// </summary>
    /// <param name="form">The bound form carrying the friend's id.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>A redirect to the opened conversation.</returns>
    [HttpPost("direct")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> StartDirect([FromForm] StartDirectForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        var conversationId = await sender.Send(new StartDirectConversationCommand(form.OtherUserId), cancellationToken);
        return RedirectToAction(nameof(Thread), new { id = conversationId });
    }

    /// <summary>Creates a group (<c>POST /chat/group</c>) and redirects to it.</summary>
    /// <param name="form">The bound form (title + seed member ids).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>A redirect to the created group.</returns>
    [HttpPost("group")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> CreateGroup([FromForm] CreateGroupForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        var conversationId = await sender.Send(
            new CreateGroupConversationCommand(form.Title, form.MemberIds), cancellationToken);
        return RedirectToAction(nameof(Thread), new { id = conversationId });
    }

    /// <summary>Adds members to a group (<c>POST /chat/{id}/members</c>) and redirects back to it.</summary>
    /// <param name="id">The group.</param>
    /// <param name="form">The bound form (friend ids to add).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>A redirect to the group thread.</returns>
    [HttpPost("{id:guid}/members")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> AddMembers(
        Guid id, [FromForm] AddMembersForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        await sender.Send(new AddGroupMembersCommand(id, form.MemberIds), cancellationToken);
        return RedirectToAction(nameof(Thread), new { id });
    }

    /// <summary>Removes a member from a group (<c>POST /chat/{id}/members/{userId}/remove</c>, admin-only).</summary>
    /// <param name="id">The group.</param>
    /// <param name="userId">The member to remove.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>A redirect to the group thread.</returns>
    [HttpPost("{id:guid}/members/{userId:guid}/remove")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId, CancellationToken cancellationToken)
    {
        await sender.Send(new RemoveGroupMemberCommand(id, userId), cancellationToken);
        return RedirectToAction(nameof(Thread), new { id });
    }

    /// <summary>Leaves a conversation (<c>POST /chat/{id}/leave</c>) and redirects to the chat home.</summary>
    /// <param name="id">The conversation to leave.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>A redirect to the chat home.</returns>
    [HttpPost("{id:guid}/leave")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Leave(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new LeaveConversationCommand(id), cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Renames a group (<c>POST /chat/{id}/rename</c>, admin-only) and redirects back to it.</summary>
    /// <param name="id">The group.</param>
    /// <param name="form">The bound form (new title).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>A redirect to the group thread.</returns>
    [HttpPost("{id:guid}/rename")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Rename(Guid id, [FromForm] RenameGroupForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        await sender.Send(new RenameGroupCommand(id, form.Title), cancellationToken);
        return RedirectToAction(nameof(Thread), new { id });
    }

    /// <summary>
    /// Marks a conversation read (<c>POST /chat/{id}/read</c>) — called by the client on opening/focusing a
    /// thread. Returns an empty 204; the read receipt is pushed to the other participants.
    /// </summary>
    /// <param name="id">The conversation the viewer has read.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>An empty <c>204 No Content</c>.</returns>
    [HttpPost("{id:guid}/read")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new MarkConversationReadCommand(id), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Soft-deletes a message (<c>POST /chat/messages/{messageId}/delete</c>, sender-only) and returns the
    /// tombstoned bubble the client swaps in place.
    /// </summary>
    /// <param name="messageId">The message to delete.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The re-rendered <c>_ChatMessage</c> partial (now a tombstone).</returns>
    [HttpPost("messages/{messageId:guid}/delete")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> DeleteMessage(Guid messageId, CancellationToken cancellationToken)
    {
        var tombstone = await sender.Send(new DeleteMessageCommand(messageId), cancellationToken);
        return PartialView("_ChatMessage", tombstone);
    }
}
