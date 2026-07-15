namespace Cinora.Domain.Enums;

/// <summary>Whether a <see cref="Cinora.Domain.Entities.Conversation"/> is a 1:1 direct chat or a named group.</summary>
public enum ConversationType
{
    /// <summary>A 1:1 conversation between exactly two users (no title; derived from the other member).</summary>
    Direct = 0,

    /// <summary>A named group conversation with two or more members and an admin.</summary>
    Group = 1,
}
