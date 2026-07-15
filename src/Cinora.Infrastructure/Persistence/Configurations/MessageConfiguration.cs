using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinora.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Message"/> with the history keyset index (§11).</summary>
internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Body).IsRequired().HasMaxLength(Message.BodyMaxLength);
        builder.Property(m => m.SentAtUtc).IsRequired();

        // Optional shared-movie payload (§ movie sharing): denormalized so the card renders standalone.
        builder.Property(m => m.SharedMovieMediaType).HasMaxLength(Message.SharedMovieFieldMaxLength);
        builder.Property(m => m.SharedMovieTitle).HasMaxLength(Message.SharedMovieTitleMaxLength);
        builder.Property(m => m.SharedMoviePosterPath).HasMaxLength(Message.SharedMovieFieldMaxLength);

        // WHY: the (ConversationId, SentAtUtc DESC, Id) keyset for message history.
        builder.HasIndex(m => new { m.ConversationId, m.SentAtUtc });

        builder.HasOne<Conversation>().WithMany().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.SenderId).OnDelete(DeleteBehavior.Restrict);
    }
}
