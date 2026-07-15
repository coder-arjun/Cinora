using Cinora.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Blocks;

/// <summary>
/// The single, fail-closed seam for block enforcement (§3). Every surface that must respect a block — user
/// search, profile read, friend-request send, the feed, and chat — routes through here so the rule cannot be
/// applied inconsistently. All checks are cheap seeks on the indexed <c>(BlockerId, BlockedUserId)</c> /
/// <c>(BlockedUserId)</c> columns.
/// </summary>
public static class BlockQueries
{
    /// <summary>True when a block exists in EITHER direction between <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static Task<bool> AreBlockedEitherWayAsync(
        IAppDbContext db, Guid a, Guid b, CancellationToken cancellationToken) =>
        db.UserBlocks.AsNoTracking().AnyAsync(
            block => (block.BlockerId == a && block.BlockedUserId == b)
                || (block.BlockerId == b && block.BlockedUserId == a),
            cancellationToken);

    /// <summary>
    /// The set of every user in a block relationship with <paramref name="me"/> (either direction), for bulk
    /// exclusion in list reads (search/feed) with no N+1.
    /// </summary>
    public static async Task<HashSet<Guid>> BlockedOrBlockedByIdsAsync(
        IAppDbContext db, Guid me, CancellationToken cancellationToken)
    {
        var ids = await db.UserBlocks.AsNoTracking()
            .Where(block => block.BlockerId == me || block.BlockedUserId == me)
            .Select(block => block.BlockerId == me ? block.BlockedUserId : block.BlockerId)
            .ToListAsync(cancellationToken);
        return [.. ids];
    }
}
