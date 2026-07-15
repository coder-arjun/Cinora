using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinora.Infrastructure.Persistence.Configurations;

/// <summary>Maps the <see cref="Genre"/> reference entity and seeds the static TMDB genre list.</summary>
internal sealed class GenreConfiguration : IEntityTypeConfiguration<Genre>
{
    public void Configure(EntityTypeBuilder<Genre> builder)
    {
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedNever();

        builder.Property(g => g.TmdbGenreId).IsRequired();

        // WHY: one row per TMDB genre id.
        builder.HasIndex(g => g.TmdbGenreId).IsUnique();

        builder.Property(g => g.Name)
               .IsRequired()
               .HasMaxLength(Genre.NameMaxLength);

        // Static reference data — versioned with the model via HasData (deterministic keys).
        builder.HasData(GenreSeedData.Genres);
    }
}
