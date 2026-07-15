namespace Cinora.Domain.Enums;

/// <summary>A member's role within a group conversation.</summary>
public enum ConversationRole
{
    /// <summary>An ordinary member: can read, send, and leave.</summary>
    Member = 0,

    /// <summary>The group admin (initially the creator): can also rename and add/remove members.</summary>
    Admin = 1,
}
