using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinora.Infrastructure.Persistence.Configurations;

/// <summary>Maps the <see cref="Notification"/> entity, including its two (recipient + optional actor) user FKs.</summary>
internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedNever();

        builder.Property(n => n.Type).IsRequired();

        builder.Property(n => n.Message)
               .IsRequired()
               .HasMaxLength(Notification.MessageMaxLength);

        builder.Property(n => n.IsRead).IsRequired();
        builder.Property(n => n.CreatedAtUtc).IsRequired();

        // WHY: the unread-badge / inbox filter query (UserId, IsRead). Distinct from the keyset
        // index below — different second column serves the unread-COUNT, not the ordered feed.
        builder.HasIndex(n => new { n.RecipientUserId, n.IsRead });

        // WHY: notification feed ordering, newest-first.
        builder.HasIndex(n => n.CreatedAtUtc);

        // WHY (Milestone 3.5): backs the inbox keyset query
        //   WHERE RecipientUserId = @me ORDER BY CreatedAtUtc DESC, Id DESC
        // — turns a per-recipient scan+sort into an ordered range seek. Narrow, non-covering by
        // design: Message is up to 500 chars, so an INCLUDE would bloat the index for little gain;
        // the clustered Id is appended automatically, so the (CreatedAtUtc, Id) keyset tie-break is
        // index-supported. ASC key ordering is fine — a uniform-DESC sort is scanned backward at equal cost.
        builder.HasIndex(n => new { n.RecipientUserId, n.CreatedAtUtc });

        // Restrict on the recipient FK — deleting a user must not erase delivered notifications.
        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(n => n.RecipientUserId)
               .OnDelete(DeleteBehavior.Restrict);

        // Optional actor (nullable FK). Restrict is CRITICAL here: a second cascading FK into the same
        // Users table would trip SQL Server's "multiple cascade paths" rule.
        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(n => n.ActorUserId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
