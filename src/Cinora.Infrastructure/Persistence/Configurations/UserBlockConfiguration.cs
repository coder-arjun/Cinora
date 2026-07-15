using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinora.Infrastructure.Persistence.Configurations;

/// <summary>Maps the directed <see cref="UserBlock"/> relationship between two users (§2, §11).</summary>
internal sealed class UserBlockConfiguration : IEntityTypeConfiguration<UserBlock>
{
    public void Configure(EntityTypeBuilder<UserBlock> builder)
    {
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();

        builder.Property(b => b.CreatedAtUtc).IsRequired();

        // WHY: at most one block per directed (blocker, blocked) pair; also the primary lookup "do I block X".
        builder.HasIndex(b => new { b.BlockerId, b.BlockedUserId }).IsUnique();

        // WHY: the reverse lookup "does X block me" and the bulk exclusion set (BlockedOrBlockedByIds).
        builder.HasIndex(b => b.BlockedUserId);

        // Both self-references to Users MUST be Restrict — two cascading FKs into one table is the SQL Server
        // "multiple cascade paths" failure (identical to FriendConfiguration).
        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(b => b.BlockerId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(b => b.BlockedUserId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
