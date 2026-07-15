using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinora.Infrastructure.Persistence.Configurations;

/// <summary>Maps the <see cref="MovieGenre"/> join with its composite (MovieId, GenreId) key.</summary>
internal sealed class MovieGenreConfiguration : IEntityTypeConfiguration<MovieGenre>
{
    public void Configure(EntityTypeBuilder<MovieGenre> builder)
    {
        // Composite key — no surrogate.
        builder.HasKey(mg => new { mg.MovieId, mg.GenreId });

        // Deleting a cached movie removes its genre links.
        builder.HasOne<Movie>()
               .WithMany()
               .HasForeignKey(mg => mg.MovieId)
               .OnDelete(DeleteBehavior.Cascade);

        // Genres are reference data — a link must never cascade-delete a genre.
        builder.HasOne<Genre>()
               .WithMany()
               .HasForeignKey(mg => mg.GenreId)
               .OnDelete(DeleteBehavior.Restrict);

        // WHY: reverse lookup — titles belonging to a genre.
        builder.HasIndex(mg => mg.GenreId);
    }
}
