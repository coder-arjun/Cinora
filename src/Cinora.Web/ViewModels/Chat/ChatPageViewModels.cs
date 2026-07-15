using Cinora.Application.Features.Chat;
using Cinora.Application.Features.Friends;

namespace Cinora.Web.ViewModels.Chat;

/// <summary>
/// The model for the two-pane <c>/chat</c> page (§6): the current user's conversation list (left) and the
/// optionally-open thread (right). <see cref="ActiveThread"/> is <c>null</c> on <c>/chat</c> (placeholder pane)
/// and populated on <c>/chat/{id}</c>. <see cref="Friends"/> seeds the new-group / add-members pickers.
/// </summary>
/// <param name="Conversations">The conversation list, newest-active first.</param>
/// <param name="ActiveThread">The open conversation's header + messages, or <c>null</c> when none is selected.</param>
/// <param name="Friends">The current user's accepted friends (the member picker for group create/add).</param>
public sealed record ChatPageVm(
    IReadOnlyList<ConversationSummaryVm> Conversations,
    ChatThreadVm? ActiveThread,
    IReadOnlyList<FriendVm> Friends);

/// <summary>The open conversation: its header/roster plus the first keyset page of messages.</summary>
/// <param name="Header">The conversation header (title, avatar, admin flag, roster).</param>
/// <param name="Messages">The newest keyset page of messages (rendered oldest-at-top by the view).</param>
public sealed record ChatThreadVm(ConversationHeaderVm Header, MessageThreadVm Messages);

/// <summary>The composer form for sending a message (the conversation id comes from the route).</summary>
public sealed class SendMessageForm
{
    /// <summary>The message text; required, bounded by the Application validator.</summary>
    public string Body { get; set; } = string.Empty;
}

/// <summary>The form for starting/opening a direct chat with a friend (the "Message" affordance).</summary>
public sealed class StartDirectForm
{
    /// <summary>The friend to open a direct chat with.</summary>
    public Guid OtherUserId { get; set; }
}

/// <summary>The form for creating a group conversation.</summary>
public sealed class CreateGroupForm
{
    /// <summary>The group name; required, bounded by the Application validator.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The friends to seed the group with (bound from the checked member checkboxes).</summary>
    public List<Guid> MemberIds { get; set; } = [];
}

/// <summary>The form for adding members to an existing group.</summary>
public sealed class AddMembersForm
{
    /// <summary>The friends to add (bound from the checked member checkboxes).</summary>
    public List<Guid> MemberIds { get; set; } = [];
}

/// <summary>The form for renaming a group.</summary>
public sealed class RenameGroupForm
{
    /// <summary>The new group name; required, bounded by the Application validator.</summary>
    public string Title { get; set; } = string.Empty;
}

/// <summary>The movie coordinates carried by a share form (hidden fields), plus the optional target friend.</summary>
public sealed class ShareMovieForm
{
    /// <summary>The shared title's TMDB id.</summary>
    public int TmdbId { get; set; }

    /// <summary>The media-type token (the <c>MediaType</c> enum name, e.g. "Movie"/"Series").</summary>
    public string MediaType { get; set; } = string.Empty;

    /// <summary>The shared title's display name.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The shared title's raw TMDB poster path, or <c>null</c>.</summary>
    public string? PosterPath { get; set; }

    /// <summary>An optional caption to accompany the shared card.</summary>
    public string? Caption { get; set; }

    /// <summary>The friend to share with (used only by the share-to-friend endpoint; find-or-create the DM).</summary>
    public Guid OtherUserId { get; set; }
}

/// <summary>
/// The model for the <c>/chat/share</c> page (the movie-sharing picker): the title being shared plus the
/// destinations — the user's friends (each opens/creates a DM) and their group conversations.
/// </summary>
/// <param name="TmdbId">The shared title's TMDB id.</param>
/// <param name="MediaType">The media-type token (e.g. "Movie"/"Series").</param>
/// <param name="Title">The shared title's display name.</param>
/// <param name="PosterPath">The shared title's raw TMDB poster path, or <c>null</c>.</param>
/// <param name="Friends">The current user's accepted friends (DM destinations).</param>
/// <param name="Groups">The current user's group conversations (group destinations).</param>
public sealed record SharePickerVm(
    int TmdbId,
    string MediaType,
    string Title,
    string? PosterPath,
    IReadOnlyList<FriendVm> Friends,
    IReadOnlyList<ConversationSummaryVm> Groups);
