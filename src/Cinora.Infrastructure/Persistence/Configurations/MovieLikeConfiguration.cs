using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinora.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="MovieLike"/> — the title-level "love" with the composite (MovieId, UserId) key.</summary>
internal sealed class MovieLikeConfiguration : IEntityTypeConfiguration<MovieLike>
{
    public void Configure(EntityTypeBuilder<MovieLike> builder)
    {
        // Composite key: a user can love a title at most once.
        builder.HasKey(like => new { like.MovieId, like.UserId });
        builder.Property(like => like.LikedAtUtc).IsRequired();

        // WHY: count-per-movie and the "most loved" ordering seek/group on MovieId.
        builder.HasIndex(like => like.MovieId);

        // Restrict both FKs (movies are cached, not deleted; users are referenced by id) — no cascade paths.
        builder.HasOne<Movie>().WithMany().HasForeignKey(like => like.MovieId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(like => like.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
