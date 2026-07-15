using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinora.Infrastructure.Persistence.Configurations;

/// <summary>Maps the <see cref="ReviewLike"/> join with its composite (ReviewId, UserId) key.</summary>
internal sealed class ReviewLikeConfiguration : IEntityTypeConfiguration<ReviewLike>
{
    public void Configure(EntityTypeBuilder<ReviewLike> builder)
    {
        // Composite key — guarantees a user can like a review at most once.
        builder.HasKey(rl => new { rl.ReviewId, rl.UserId });

        builder.Property(rl => rl.LikedAtUtc).IsRequired();

        // Deleting a review removes its likes.
        builder.HasOne<Review>()
               .WithMany()
               .HasForeignKey(rl => rl.ReviewId)
               .OnDelete(DeleteBehavior.Cascade);

        // Restrict on the User FK — avoids a second cascade path into the Users table.
        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(rl => rl.UserId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
