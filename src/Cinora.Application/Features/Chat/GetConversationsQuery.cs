using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Chat;

/// <summary>
/// Fetches the current user's conversation list, newest-active first (§6). Each row carries its display title
/// and avatar (for a DM these are the OTHER member's; for a group, the group name and a monogram), a short
/// last-message preview, and the viewer's unread count. Everything is resolved with correlated sub-queries in a
/// single <c>AsNoTracking</c> read — no N+1 (§12, §13). Read-only (CQRS-pure).
/// </summary>
public sealed record GetConversationsQuery : IRequest<ConversationsVm>;

/// <summary>Handles <see cref="GetConversationsQuery"/>.</summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved viewer whose conversations these are (ADR 0009).</param>
public sealed class GetConversationsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetConversationsQuery, ConversationsVm>
{
    /// <summary>The maximum number of characters shown in a conversation's last-message preview.</summary>
    public const int PreviewMaxLength = 80;

    /// <inheritdoc />
    public async Task<ConversationsVm> Handle(GetConversationsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();

        var rows = await db.Conversations.AsNoTracking()
            .Where(c => db.ConversationMembers.Any(m => m.ConversationId == c.Id && m.UserId == me))
            .OrderByDescending(c => c.LastMessageAtUtc)
            .ThenByDescending(c => c.Id)
            .Select(c => new ConversationRow
            {
                Id = c.Id,
                Type = c.Type,
                GroupTitle = c.Title,
                LastMessageAtUtc = c.LastMessageAtUtc,

                // For a DM: the other member's display fields via one nested correlated sub-query (proven in
                // FriendProjections). For a group these are ignored in favour of the group Title.
                DirectOtherName = db.ConversationMembers
                    .Where(m => m.ConversationId == c.Id && m.UserId != me)
                    .Select(m => db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefault())
                    .FirstOrDefault(),
                DirectOtherAvatar = db.ConversationMembers
                    .Where(m => m.ConversationId == c.Id && m.UserId != me)
                    .Select(m => db.Users.Where(u => u.Id == m.UserId).Select(u => u.AvatarFileKey).FirstOrDefault())
                    .FirstOrDefault(),

                LastBody = db.Messages
                    .Where(m => m.ConversationId == c.Id)
                    .OrderByDescending(m => m.SentAtUtc).ThenByDescending(m => m.Id)
                    .Select(m => (string?)m.Body).FirstOrDefault(),
                LastDeleted = db.Messages
                    .Where(m => m.ConversationId == c.Id)
                    .OrderByDescending(m => m.SentAtUtc).ThenByDescending(m => m.Id)
                    .Select(m => (bool?)m.IsDeleted).FirstOrDefault(),

                // Unread = messages from others sent after my read watermark. The watermark is inlined as a
                // correlated sub-query (my membership always exists here — I filtered to my conversations).
                UnreadCount = db.Messages.Count(m => m.ConversationId == c.Id
                    && m.SenderId != me
                    && m.SentAtUtc > db.ConversationMembers
                        .Where(mm => mm.ConversationId == c.Id && mm.UserId == me)
                        .Select(mm => mm.LastReadAtUtc).FirstOrDefault()),
            })
            .ToListAsync(cancellationToken);

        var summaries = rows.Select(row => new ConversationSummaryVm(
            row.Id,
            row.Type,
            row.Type == ConversationType.Direct ? row.DirectOtherName ?? string.Empty : row.GroupTitle ?? string.Empty,
            row.Type == ConversationType.Direct ? row.DirectOtherAvatar : null,
            row.LastBody is null ? null : row.LastDeleted == true ? "Message deleted" : Truncate(row.LastBody),
            row.LastMessageAtUtc,
            row.UnreadCount)).ToList();

        return new ConversationsVm(summaries);
    }

    private static string Truncate(string value) =>
        value.Length <= PreviewMaxLength ? value : value[..PreviewMaxLength];
}

/// <summary>The intermediate shape EF materializes per conversation; assembled into a <see cref="ConversationSummaryVm"/> in memory.</summary>
internal sealed class ConversationRow
{
    /// <summary>The conversation id.</summary>
    public required Guid Id { get; init; }

    /// <summary>Whether this is a direct chat or a group.</summary>
    public required ConversationType Type { get; init; }

    /// <summary>The group's title, or <c>null</c> for a direct chat.</summary>
    public string? GroupTitle { get; init; }

    /// <summary>The conversation's last-message instant (the recency sort key).</summary>
    public required DateTime LastMessageAtUtc { get; init; }

    /// <summary>For a direct chat, the other member's display name (ignored for a group).</summary>
    public string? DirectOtherName { get; init; }

    /// <summary>For a direct chat, the other member's avatar key (ignored for a group).</summary>
    public string? DirectOtherAvatar { get; init; }

    /// <summary>The most recent message's body, or <c>null</c> when the conversation has no messages.</summary>
    public string? LastBody { get; init; }

    /// <summary>Whether the most recent message is soft-deleted (renders a "Message deleted" preview).</summary>
    public bool? LastDeleted { get; init; }

    /// <summary>The viewer's unread count (messages from others after their read watermark).</summary>
    public required int UnreadCount { get; init; }
}
