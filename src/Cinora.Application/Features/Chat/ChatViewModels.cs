using Cinora.Domain.Enums;

namespace Cinora.Application.Features.Chat;

/// <summary>
/// One rendered chat message (§6). <see cref="Body"/> is PLAIN TEXT — the view Razor-encodes it and the client
/// appends it via <c>textContent</c>, never <c>innerHTML</c> (§13). When <see cref="IsDeleted"/> is
/// <see langword="true"/> the <see cref="Body"/> is empty and the view renders a "message deleted" tombstone.
/// </summary>
/// <param name="Id">The message id (also the client-side dedupe key against the realtime echo).</param>
/// <param name="SenderId">The sending user's id.</param>
/// <param name="SenderDisplayName">The sender's public display name (PLAIN TEXT; encoded on render).</param>
/// <param name="SenderAvatarFileKey">The sender's avatar storage key, or <c>null</c> (rendered via <c>_Avatar</c>).</param>
/// <param name="Body">The message text (empty when <paramref name="IsDeleted"/>); PLAIN TEXT.</param>
/// <param name="SentAtUtc">The UTC instant the message was sent.</param>
/// <param name="IsMine">Whether the viewer is the sender (drives left/right bubble alignment).</param>
/// <param name="IsDeleted">Whether the message was soft-deleted (renders a tombstone).</param>
/// <param name="SharedMovieTmdbId">The TMDB id of a shared movie/series, or <c>null</c> for a plain message.</param>
/// <param name="SharedMovieMediaType">The shared title's media-type token (e.g. "Movie"/"Series"), or <c>null</c>.</param>
/// <param name="SharedMovieTitle">The shared title's display name (PLAIN TEXT), or <c>null</c>.</param>
/// <param name="SharedMoviePosterPath">The shared title's raw TMDB poster path, or <c>null</c>.</param>
/// <param name="IsReadByOthers">For the viewer's OWN sent message, whether every other member has read it (drives the blue double-tick). Always <c>false</c> for others' messages.</param>
public sealed record ChatMessageVm(
    Guid Id,
    Guid SenderId,
    string SenderDisplayName,
    string? SenderAvatarFileKey,
    string Body,
    DateTime SentAtUtc,
    bool IsMine,
    bool IsDeleted,
    int? SharedMovieTmdbId = null,
    string? SharedMovieMediaType = null,
    string? SharedMovieTitle = null,
    string? SharedMoviePosterPath = null,
    bool IsReadByOthers = false);

/// <summary>
/// One keyset page of a conversation's message history, newest-first (§6, §12). The cursor is opaque
/// <c>(SentAtUtc, Id)</c> — never an <c>OFFSET</c> — and is <c>null</c> when there are no older messages.
/// </summary>
/// <param name="Messages">The page of messages, ordered newest-first.</param>
/// <param name="HasMore">Whether an older page exists beyond this one.</param>
/// <param name="NextCursorSentAt">The <c>SentAtUtc</c> of the last kept message, or <c>null</c> when no more.</param>
/// <param name="NextCursorId">The <c>Id</c> of the last kept message (the tie-break), or <c>null</c> when no more.</param>
public sealed record MessageThreadVm(
    IReadOnlyList<ChatMessageVm> Messages,
    bool HasMore,
    DateTime? NextCursorSentAt,
    Guid? NextCursorId);

/// <summary>The current user's conversation list, newest-active first (§6).</summary>
/// <param name="Conversations">The user's conversations, ordered by most recent message.</param>
public sealed record ConversationsVm(IReadOnlyList<ConversationSummaryVm> Conversations);

/// <summary>
/// The header/metadata of a single conversation the viewer belongs to (§6): its display title and avatar, whether
/// the viewer is the group admin, and the full member roster (for the group-management UI). Membership is
/// enforced by the query that produces it.
/// </summary>
/// <param name="Id">The conversation id.</param>
/// <param name="Type">Whether this is a direct chat or a group.</param>
/// <param name="Title">The display title (the other member's name for a DM; the group name for a group).</param>
/// <param name="AvatarFileKey">The header avatar key (the other member's for a DM; <c>null</c> for a group).</param>
/// <param name="IsAdmin">Whether the viewer is the group admin (unlocks rename/add/remove).</param>
/// <param name="Members">The full member roster (for the group-management panel).</param>
public sealed record ConversationHeaderVm(
    Guid Id,
    ConversationType Type,
    string Title,
    string? AvatarFileKey,
    bool IsAdmin,
    IReadOnlyList<ChatMemberVm> Members);

/// <summary>One member of a conversation (for the roster / group-management UI).</summary>
/// <param name="UserId">The member's user id.</param>
/// <param name="DisplayName">The member's display name (PLAIN TEXT; encoded on render).</param>
/// <param name="AvatarFileKey">The member's avatar storage key, or <c>null</c>.</param>
/// <param name="IsAdmin">Whether this member is the group admin.</param>
public sealed record ChatMemberVm(
    Guid UserId,
    string DisplayName,
    string? AvatarFileKey,
    bool IsAdmin);

/// <summary>
/// One row of the conversation list (§6). For a <see cref="ConversationType.Direct"/> chat the
/// <see cref="Title"/>/<see cref="AvatarFileKey"/> are the OTHER member's display name/avatar; for a
/// <see cref="ConversationType.Group"/> they are the group's name and <c>null</c> (a monogram is rendered).
/// </summary>
/// <param name="Id">The conversation id.</param>
/// <param name="Type">Whether this is a direct chat or a group.</param>
/// <param name="Title">The display title (the other member's name for a DM; the group name for a group).</param>
/// <param name="AvatarFileKey">The avatar storage key to render (the other member's for a DM; <c>null</c> for a group).</param>
/// <param name="LastMessagePreview">A short PLAIN-TEXT preview of the most recent message, or <c>null</c> if none.</param>
/// <param name="LastMessageAtUtc">The recency sort key (the conversation's last-message instant).</param>
/// <param name="UnreadCount">The number of messages from others sent after the viewer's last-read watermark.</param>
public sealed record ConversationSummaryVm(
    Guid Id,
    ConversationType Type,
    string Title,
    string? AvatarFileKey,
    string? LastMessagePreview,
    DateTime LastMessageAtUtc,
    int UnreadCount);
