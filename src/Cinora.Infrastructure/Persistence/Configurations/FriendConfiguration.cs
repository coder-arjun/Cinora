using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinora.Infrastructure.Persistence.Configurations;

/// <summary>Maps the self-referencing <see cref="Friend"/> relationship between two users.</summary>
internal sealed class FriendConfiguration : IEntityTypeConfiguration<Friend>
{
    public void Configure(EntityTypeBuilder<Friend> builder)
    {
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();

        builder.Property(f => f.Status).IsRequired();
        builder.Property(f => f.RequestedAtUtc).IsRequired();

        // WHY: at most one directed request per ordered (requester, addressee) pair.
        builder.HasIndex(f => new { f.RequesterId, f.AddresseeId }).IsUnique();

        // WHY: the incoming-request lookup filters on (AddresseeId == me, Status == Pending), and the
        // 3.4 activity feed reuses this to resolve reverse friendships (I am the addressee). A composite
        // seek on (AddresseeId, Status) beats a seek-on-AddresseeId-then-residual-filter. Its leading
        // AddresseeId column fully covers the AddresseeId foreign key, so EF's FK-index convention drops
        // the now-strictly-redundant standalone IX_Friends_AddresseeId. EF's default name for this index
        // is already IX_Friends_AddresseeId_Status.
        builder.HasIndex(f => new { f.AddresseeId, f.Status });

        // WHY: the mirror path — outgoing-request lookup (RequesterId == me, Status == Pending) and the
        // 3.4 feed's forward-friend resolution (I am the requester). Pure addition: the RequesterId FK is
        // already covered by the unique (RequesterId, AddresseeId) index, so nothing is dropped here.
        builder.HasIndex(f => new { f.RequesterId, f.Status });

        // Both self-references to Users MUST be Restrict — two cascading FKs into the same table is
        // exactly the SQL Server "multiple cascade paths" failure we are avoiding.
        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(f => f.RequesterId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(f => f.AddresseeId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
