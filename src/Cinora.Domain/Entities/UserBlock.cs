using Cinora.Domain.Exceptions;

namespace Cinora.Domain.Entities;

/// <summary>
/// A directed block: <see cref="BlockerId"/> has blocked <see cref="BlockedUserId"/>. Existence of the row is
/// the block (there is no status/lifecycle); unblocking deletes the row. A block is independent of the
/// <see cref="Friend"/> graph — you can block someone you were never friends with — and it enforces the full
/// cut-off + hide semantics of the Friends &amp; Social overhaul (§2).
/// </summary>
public sealed class UserBlock
{
    private UserBlock()
    {
    }

    /// <summary>The unique identifier of this block record.</summary>
    public Guid Id { get; private set; }

    /// <summary>The user who created the block.</summary>
    public Guid BlockerId { get; private set; }

    /// <summary>The user who is blocked.</summary>
    public Guid BlockedUserId { get; private set; }

    /// <summary>The UTC instant the block was created.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Creates a new block from <paramref name="blockerId"/> against <paramref name="blockedUserId"/>.</summary>
    /// <param name="blockerId">The user creating the block.</param>
    /// <param name="blockedUserId">The user being blocked; must differ from the blocker.</param>
    /// <returns>A new <see cref="UserBlock"/>.</returns>
    /// <exception cref="DomainException">Thrown when a user attempts to block themselves.</exception>
    public static UserBlock Create(Guid blockerId, Guid blockedUserId)
    {
        if (blockerId == blockedUserId)
        {
            throw new DomainException("A user cannot block themselves.");
        }

        return new UserBlock
        {
            Id = Guid.NewGuid(),
            BlockerId = blockerId,
            BlockedUserId = blockedUserId,
            CreatedAtUtc = DateTime.UtcNow,
        };
    }
}
