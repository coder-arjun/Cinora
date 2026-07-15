using System.Diagnostics.CodeAnalysis;
using Cinora.Domain.Enums;
using Cinora.Domain.Exceptions;

namespace Cinora.Domain.Entities;

/// <summary>
/// A directed friendship between two users: the <see cref="RequesterId"/> who sent the request and
/// the <see cref="AddresseeId"/> who received it. Tracks the request lifecycle and enforces that a
/// user cannot befriend themselves and that only a pending request can be answered.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "'Friend' is a mandated Backend Schema entity name (Milestone 1.1) that the EF configurations in Milestone 1.2 depend on. Cinora is a C#-only solution, so the Visual Basic keyword clash CA1716 warns about is not a practical concern.")]
public sealed class Friend
{
    private Friend()
    {
    }

    /// <summary>The unique identifier of this friendship record.</summary>
    public Guid Id { get; private set; }

    /// <summary>The identifier of the user who sent the friend request.</summary>
    public Guid RequesterId { get; private set; }

    /// <summary>The identifier of the user who received the friend request.</summary>
    public Guid AddresseeId { get; private set; }

    /// <summary>The current status of the friendship.</summary>
    public FriendStatus Status { get; private set; }

    /// <summary>The UTC instant the request was created.</summary>
    public DateTime RequestedAtUtc { get; private set; }

    /// <summary>The UTC instant the request was accepted or declined, or <c>null</c> while pending.</summary>
    public DateTime? RespondedAtUtc { get; private set; }

    /// <summary>Creates a new pending friend request.</summary>
    /// <param name="requesterId">The user sending the request.</param>
    /// <param name="addresseeId">The user receiving the request; must differ from the requester.</param>
    /// <returns>A new <see cref="Friend"/> in the <see cref="FriendStatus.Pending"/> state.</returns>
    /// <exception cref="DomainException">Thrown when a user attempts to befriend themselves.</exception>
    public static Friend Request(Guid requesterId, Guid addresseeId)
    {
        if (requesterId == addresseeId)
        {
            throw new DomainException("A user cannot send a friend request to themselves.");
        }

        return new Friend
        {
            Id = Guid.NewGuid(),
            RequesterId = requesterId,
            AddresseeId = addresseeId,
            Status = FriendStatus.Pending,
            RequestedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>Accepts a pending friend request.</summary>
    /// <exception cref="DomainException">Thrown when the request is not pending.</exception>
    public void Accept() => TransitionTo(FriendStatus.Accepted);

    /// <summary>Declines a pending friend request.</summary>
    /// <exception cref="DomainException">Thrown when the request is not pending.</exception>
    public void Decline() => TransitionTo(FriendStatus.Declined);

    private void TransitionTo(FriendStatus status)
    {
        if (Status != FriendStatus.Pending)
        {
            throw new DomainException("Only a pending friend request can be accepted or declined.");
        }

        Status = status;
        RespondedAtUtc = DateTime.UtcNow;
    }
}
