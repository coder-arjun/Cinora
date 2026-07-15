using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinora.Infrastructure.Persistence.Configurations;

/// <summary>Maps the <see cref="Comment"/> entity on a review.</summary>
internal sealed class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.Body)
               .IsRequired()
               .HasMaxLength(Comment.BodyMaxLength);

        builder.Property(c => c.CreatedAtUtc).IsRequired();

        // WHY: a review's comments, in chronological order — the known access path.
        builder.HasIndex(c => new { c.ReviewId, c.CreatedAtUtc });

        // Deleting a review removes its comments.
        builder.HasOne<Review>()
               .WithMany()
               .HasForeignKey(c => c.ReviewId)
               .OnDelete(DeleteBehavior.Cascade);

        // Restrict on the User FK — avoids a second cascade path into the Users table.
        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(c => c.UserId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
