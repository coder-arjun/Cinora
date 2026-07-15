using Cinora.Domain.Enums;
using Cinora.Domain.Exceptions;

namespace Cinora.Domain.Entities;

/// <summary>
/// A chat conversation — either a 1:1 <see cref="ConversationType.Direct"/> chat or a named
/// <see cref="ConversationType.Group"/>. Membership is modelled separately by <c>ConversationMember</c>;
/// messages by <c>Message</c> (both added in Task C1.2). <see cref="LastMessageAtUtc"/> is denormalized
/// (bumped on each send) so the conversation list sorts by recency without scanning messages.
/// </summary>
public sealed class Conversation
{
    /// <summary>The maximum length of a group <see cref="Title"/>.</summary>
    public const int TitleMaxLength = 80;

    private Conversation()
    {
    }

    /// <summary>The unique identifier of this conversation.</summary>
    public Guid Id { get; private set; }

    /// <summary>Whether this is a 1:1 direct chat or a group.</summary>
    public ConversationType Type { get; private set; }

    /// <summary>The group name, or <c>null</c> for a direct chat (the UI derives the title from the other member).</summary>
    public string? Title { get; private set; }

    /// <summary>The user who started the conversation (the initial group admin).</summary>
    public Guid CreatedByUserId { get; private set; }

    /// <summary>The UTC instant the conversation was created.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>The UTC instant of the most recent message (bumped on each send); drives the recency sort.</summary>
    public DateTime LastMessageAtUtc { get; private set; }

    /// <summary>Creates a 1:1 direct conversation between two distinct users.</summary>
    /// <param name="userAId">One participant.</param>
    /// <param name="userBId">The other participant; must differ from <paramref name="userAId"/>.</param>
    /// <returns>A new direct <see cref="Conversation"/>.</returns>
    /// <exception cref="DomainException">Thrown when the two ids are equal.</exception>
    public static Conversation CreateDirect(Guid userAId, Guid userBId)
    {
        if (userAId == userBId)
        {
            throw new DomainException("A direct conversation needs two distinct users.");
        }

        var now = DateTime.UtcNow;
        return new Conversation
        {
            Id = Guid.NewGuid(),
            Type = ConversationType.Direct,
            Title = null,
            CreatedByUserId = userAId,
            CreatedAtUtc = now,
            LastMessageAtUtc = now,
        };
    }

    /// <summary>Creates a named group conversation owned by <paramref name="creatorId"/>.</summary>
    /// <param name="creatorId">The creator (initial admin).</param>
    /// <param name="title">The group name; required, max <see cref="TitleMaxLength"/> characters.</param>
    /// <returns>A new group <see cref="Conversation"/>.</returns>
    /// <exception cref="DomainException">Thrown when the title is blank or too long.</exception>
    public static Conversation CreateGroup(Guid creatorId, string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > TitleMaxLength)
        {
            throw new DomainException($"A group title is required and must be at most {TitleMaxLength} characters.");
        }

        var now = DateTime.UtcNow;
        return new Conversation
        {
            Id = Guid.NewGuid(),
            Type = ConversationType.Group,
            Title = title.Trim(),
            CreatedByUserId = creatorId,
            CreatedAtUtc = now,
            LastMessageAtUtc = now,
        };
    }

    /// <summary>Renames a group conversation.</summary>
    /// <param name="title">The new title; required, max <see cref="TitleMaxLength"/> characters.</param>
    /// <exception cref="DomainException">Thrown when the conversation is not a group or the title is invalid.</exception>
    public void Rename(string title)
    {
        if (Type != ConversationType.Group)
        {
            throw new DomainException("Only a group conversation can be renamed.");
        }

        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > TitleMaxLength)
        {
            throw new DomainException($"A group title is required and must be at most {TitleMaxLength} characters.");
        }

        Title = title.Trim();
    }

    /// <summary>Advances <see cref="LastMessageAtUtc"/> to the given instant (called when a message is sent).</summary>
    /// <param name="atUtc">The new most-recent-message instant.</param>
    public void BumpLastMessage(DateTime atUtc) => LastMessageAtUtc = atUtc;
}
