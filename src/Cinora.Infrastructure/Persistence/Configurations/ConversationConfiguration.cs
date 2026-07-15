using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinora.Infrastructure.Persistence.Configurations;

/// <summary>Maps the <see cref="Conversation"/> aggregate (§11).</summary>
internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Type).IsRequired();
        builder.Property(c => c.Title).HasMaxLength(Conversation.TitleMaxLength);
        builder.Property(c => c.CreatedAtUtc).IsRequired();
        builder.Property(c => c.LastMessageAtUtc).IsRequired();
        builder.HasIndex(c => c.LastMessageAtUtc);

        builder.HasOne<User>().WithMany().HasForeignKey(c => c.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
