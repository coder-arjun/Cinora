using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinora.Infrastructure.Persistence.Configurations;

/// <summary>Maps the <see cref="Movie"/> catalogue entity cached from TMDB.</summary>
internal sealed class MovieConfiguration : IEntityTypeConfiguration<Movie>
{
    public void Configure(EntityTypeBuilder<Movie> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.TmdbId).IsRequired();

        // WHY: TMDB movie and TV id-spaces overlap, so a series and a movie can share a numeric
        // TmdbId. Uniqueness is therefore per (TmdbId, MediaType), not TmdbId alone.
        builder.HasIndex(m => new { m.TmdbId, m.MediaType }).IsUnique();

        builder.Property(m => m.MediaType).IsRequired();

        builder.Property(m => m.Title)
               .IsRequired()
               .HasMaxLength(Movie.TitleMaxLength);

        // TMDB synopsis: bounded at the nvarchar ceiling rather than nvarchar(max); the Phase 2 sync
        // truncates longer text (a cache, not the source of truth).
        builder.Property(m => m.Overview).HasMaxLength(4000);

        // TMDB image paths are short relative paths (for example "/abc123.jpg").
        builder.Property(m => m.PosterPath).HasMaxLength(256);
        builder.Property(m => m.BackdropPath).HasMaxLength(256);

        builder.Property(m => m.CachedAtUtc).IsRequired();
    }
}
