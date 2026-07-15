namespace Cinora.Domain.Enums;

/// <summary>The lifecycle status of a <see cref="Entities.Friend"/> relationship.</summary>
public enum FriendStatus
{
    /// <summary>The request has been sent but not yet answered.</summary>
    Pending = 0,

    /// <summary>The addressee accepted the request; the two users are friends.</summary>
    Accepted = 1,

    /// <summary>The addressee declined the request.</summary>
    Declined = 2,
}
