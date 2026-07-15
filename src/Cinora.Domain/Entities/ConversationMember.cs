using Cinora.Domain.Enums;

namespace Cinora.Domain.Entities;

/// <summary>
/// A user's membership in a <see cref="Conversation"/> — the join that authorizes reading/sending (participation
/// is the authorization, §3). <see cref="LastReadAtUtc"/> drives both unread counts and read receipts.
/// </summary>
public sealed class ConversationMember
{
    private ConversationMember()
    {
    }

    /// <summary>The unique identifier of this membership row.</summary>
    public Guid Id { get; private set; }

    /// <summary>The conversation this membership belongs to.</summary>
    public Guid ConversationId { get; private set; }

    /// <summary>The member user's id.</summary>
    public Guid UserId { get; private set; }

    /// <summary>The member's role (group admin vs ordinary member).</summary>
    public ConversationRole Role { get; private set; }

    /// <summary>The UTC instant the user joined (also the admin-promotion tiebreaker: earliest-joined wins).</summary>
    public DateTime JoinedAtUtc { get; private set; }

    /// <summary>The UTC instant up to which the user has read; drives unread counts + read receipts.</summary>
    public DateTime LastReadAtUtc { get; private set; }

    /// <summary>Creates a membership row.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="userId">The member.</param>
    /// <param name="role">The member's role.</param>
    /// <returns>A new <see cref="ConversationMember"/> with <see cref="LastReadAtUtc"/> seeded to now.</returns>
    public static ConversationMember Create(Guid conversationId, Guid userId, ConversationRole role)
    {
        var now = DateTime.UtcNow;
        return new ConversationMember
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = userId,
            Role = role,
            JoinedAtUtc = now,
            LastReadAtUtc = now,
        };
    }

    /// <summary>Advances the read watermark (never backwards).</summary>
    /// <param name="atUtc">The instant read up to.</param>
    public void MarkRead(DateTime atUtc)
    {
        if (atUtc > LastReadAtUtc)
        {
            LastReadAtUtc = atUtc;
        }
    }

    /// <summary>Promotes this member to <see cref="ConversationRole.Admin"/> (used when the admin leaves).</summary>
    public void Promote() => Role = ConversationRole.Admin;
}
