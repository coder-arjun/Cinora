using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Chat;

/// <summary>
/// Fetches one keyset page of a conversation's message history, newest-first (§6, §12). Membership is checked
/// first (a non-member → 403, a missing conversation → 404). Paging is over <c>(SentAtUtc DESC, Id DESC)</c> with
/// an opaque cursor — never an <c>OFFSET</c>; each message projects to a <see cref="ChatMessageVm"/> whose
/// sender fields come from a single correlated <c>Users</c> sub-select (no N+1, §13). A soft-deleted message
/// projects an empty <c>Body</c> so the view can render a tombstone in its place.
/// </summary>
/// <param name="ConversationId">The conversation whose history to read.</param>
/// <param name="CursorSentAt">The previous page's last message <c>SentAtUtc</c>, or <c>null</c> for the first page.</param>
/// <param name="CursorId">The previous page's last message <c>Id</c> (the tie-break), or <c>null</c> for the first page.</param>
/// <param name="Take">The desired page size (clamped to a sane maximum).</param>
public sealed record GetMessagesQuery(
    Guid ConversationId,
    DateTime? CursorSentAt,
    Guid? CursorId,
    int Take = 30) : IRequest<MessageThreadVm>;

/// <summary>
/// Handles <see cref="GetMessagesQuery"/>. Confirms the conversation exists and the viewer is a member, then
/// reads one <c>Take + 1</c> keyset page (newest-first) with a single <c>AsNoTracking</c> query and the correlated
/// sender sub-select. Read-only (CQRS-pure).
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved viewer whose thread this is (ADR 0009).</param>
public sealed class GetMessagesQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetMessagesQuery, MessageThreadVm>
{
    /// <summary>The largest page the handler will serve regardless of the requested <c>Take</c>.</summary>
    public const int MaxTake = 50;

    /// <inheritdoc />
    public async Task<MessageThreadVm> Handle(GetMessagesQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();
        var take = Math.Clamp(request.Take, 1, MaxTake);

        var conversationExists = await db.Conversations.AsNoTracking()
            .AnyAsync(c => c.Id == request.ConversationId, cancellationToken);
        if (!conversationExists)
        {
            throw new NotFoundException($"Conversation ({request.ConversationId}) was not found.");
        }

        var joinedAtUtc = await db.ConversationMembers.AsNoTracking()
            .Where(m => m.ConversationId == request.ConversationId && m.UserId == me)
            .Select(m => (DateTime?)m.JoinedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (joinedAtUtc is null)
        {
            throw new ForbiddenAccessException("You are not a member of this conversation.");
        }

        // Privacy (ADR 0024 / review M2): a member sees only messages sent AT OR AFTER they joined, so a
        // newly-added member cannot read the group's pre-join history.
        var query = db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == request.ConversationId && m.SentAtUtc >= joinedAtUtc.Value);

        // Strictly-older than the cursor under (SentAtUtc DESC, Id DESC): an earlier instant, or the same
        // instant with a smaller id. The Id tie-break keeps rows sharing a SentAtUtc stable across the boundary.
        if (request.CursorSentAt is { } cursorSentAt && request.CursorId is { } cursorId)
        {
            query = query.Where(m =>
                m.SentAtUtc < cursorSentAt
                || (m.SentAtUtc == cursorSentAt && m.Id.CompareTo(cursorId) < 0));
        }

        var rows = await query
            .OrderByDescending(m => m.SentAtUtc)
            .ThenByDescending(m => m.Id)
            .Take(take + 1)
            .Select(m => new ChatMessageVm(
                m.Id,
                m.SenderId,
                db.Users.Where(u => u.Id == m.SenderId).Select(u => u.DisplayName).FirstOrDefault()!,
                db.Users.Where(u => u.Id == m.SenderId).Select(u => u.AvatarFileKey).FirstOrDefault(),
                m.IsDeleted ? string.Empty : m.Body,
                m.SentAtUtc,
                m.SenderId == me,
                m.IsDeleted,
                m.IsDeleted ? null : m.SharedMovieTmdbId,
                m.IsDeleted ? null : m.SharedMovieMediaType,
                m.IsDeleted ? null : m.SharedMovieTitle,
                m.IsDeleted ? null : m.SharedMoviePosterPath,
                // Read (blue double-tick): my own message that every OTHER member has read (their read
                // watermark is at or past it). No other member behind it ⇒ read by all.
                m.SenderId == me
                    && !db.ConversationMembers.Any(mm =>
                        mm.ConversationId == m.ConversationId && mm.UserId != me && mm.LastReadAtUtc < m.SentAtUtc)))
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > take;
        var messages = rows.Take(take).ToList();

        var lastKept = messages.Count > 0 ? messages[^1] : null;
        var nextCursorSentAt = hasMore && lastKept is not null ? lastKept.SentAtUtc : (DateTime?)null;
        var nextCursorId = hasMore && lastKept is not null ? lastKept.Id : (Guid?)null;

        return new MessageThreadVm(messages, hasMore, nextCursorSentAt, nextCursorId);
    }
}
