using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinora.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="ConversationMember"/> — one row per (conversation, user) (§11).</summary>
internal sealed class ConversationMemberConfiguration : IEntityTypeConfiguration<ConversationMember>
{
    public void Configure(EntityTypeBuilder<ConversationMember> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Role).IsRequired();
        builder.Property(m => m.JoinedAtUtc).IsRequired();
        builder.Property(m => m.LastReadAtUtc).IsRequired();

        // WHY: one membership per (conversation, user); also the primary "am I in this conversation" seek.
        builder.HasIndex(m => new { m.ConversationId, m.UserId }).IsUnique();
        // WHY: "my conversations" list.
        builder.HasIndex(m => m.UserId);

        builder.HasOne<Conversation>().WithMany().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
