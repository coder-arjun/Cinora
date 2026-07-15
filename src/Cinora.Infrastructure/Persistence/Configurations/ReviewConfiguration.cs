using Cinora.Domain.Entities;
using Cinora.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinora.Infrastructure.Persistence.Configurations;

/// <summary>Maps the <see cref="Review"/> aggregate, including the <see cref="Rating"/> conversion and its guard rail.</summary>
internal sealed class ReviewConfiguration : IEntityTypeConfiguration<Review>
{
    public void Configure(EntityTypeBuilder<Review> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        // WHY: enforces "one review per user per movie" at the database level (app checks can race).
        builder.HasIndex(r => new { r.UserId, r.MovieId }).IsUnique();

        // Value object stored as its underlying int; validated on read via Rating.From.
        builder.Property(r => r.Rating)
               .HasConversion(r => r.Value, v => Rating.From(v))
               .HasColumnName("Rating")
               .IsRequired();

        builder.Property(r => r.Body)
               .IsRequired()
               .HasMaxLength(Review.BodyMaxLength);

        builder.Property(r => r.CreatedAtUtc).IsRequired();

        // Defense-in-depth for the 1–10 invariant (review finding M1).
        builder.ToTable(t => t.HasCheckConstraint("CK_Review_Rating", "[Rating] BETWEEN 1 AND 10"));

        // WHY: activity-feed ordering is newest-first on creation time.
        builder.HasIndex(r => r.CreatedAtUtc);

        // WHY: the public title-reviews query orders by (MovieId, CreatedAtUtc DESC, Id DESC).
        // This composite turns "reviews for a movie, newest first" into an ordered index seek
        // (backward range scan on the ascending key) instead of a seek-on-MovieId-then-explicit-Sort.
        // Its leading MovieId column fully covers the MovieId foreign key, so EF's FK-index
        // convention drops the now-strictly-redundant standalone IX_Reviews_MovieId. EF's default
        // name for this index is already IX_Reviews_MovieId_CreatedAtUtc.
        builder.HasIndex(r => new { r.MovieId, r.CreatedAtUtc });

        // WHY (Milestone 3.4 activity feed): the friends-feed query is
        //   Reviews WHERE UserId IN (@friendIds) ORDER BY CreatedAtUtc DESC, Id DESC (keyset paged).
        // This composite turns the per-friend scan+sort into an ordered range seek: seek on UserId,
        // then a backward range scan of the ascending CreatedAtUtc key (uniform-DESC scan-backward is
        // equal-cost to a stored DESC key). The (CreatedAtUtc, Id) keyset tie-break is index-supported
        // WITHOUT declaring Id: Reviews is clustered on Id, so the clustering key is appended to this
        // non-unique index's leaf ordering automatically. No INCLUDE — Body is nvarchar(4000); covering
        // it would bloat the index for the handful of clustered-key lookups per page (a bad trade).
        // EF's default name for this index is already IX_Reviews_UserId_CreatedAtUtc.
        builder.HasIndex(r => new { r.UserId, r.CreatedAtUtc });

        // Deleting a user must never silently erase their review history.
        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(r => r.UserId)
               .OnDelete(DeleteBehavior.Restrict);

        // A cached title cannot be deleted while reviews reference it (protects user content).
        builder.HasOne<Movie>()
               .WithMany()
               .HasForeignKey(r => r.MovieId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
