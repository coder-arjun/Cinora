---
name: ef-core-data-access
description: Use when designing the DbContext, configuring entity relationships, keys, or indexes, writing EF Core queries, or deciding between repositories and direct DbContext use in Cinora handlers.
---

# EF Core Data Access

## Overview
Configure the model explicitly with one `IEntityTypeConfiguration<T>` per entity; read with `AsNoTracking` projections, write through tracked entities.

## Quick Reference
| Task | Approach |
|---|---|
| Model configuration | One `IEntityTypeConfiguration<T>` class per entity; `ApplyConfigurationsFromAssembly` in `OnModelCreating` |
| MovieGenre join | Composite key `HasKey(mg => new { mg.MovieId, mg.GenreId })` |
| One review per user per movie | `HasIndex(r => new { r.UserId, r.MovieId }).IsUnique()` |
| Friend self-reference | Two FKs to User (`RequesterId`, `AddresseeId`), `DeleteBehavior.Restrict` to avoid cascade cycles |
| Reads | `AsNoTracking()` + `Select` projection to a DTO — never load entities just to display them |
| Writes | Load tracked entity, call domain method, `SaveChangesAsync` |
| Repository? | Only when a query is reused across handlers or wraps real logic; otherwise inject the DbContext (or an `IAppDbContext` abstraction) directly |

## Pattern
```csharp
// Cinora.Infrastructure/Persistence/Configurations/ReviewConfiguration.cs
public class ReviewConfiguration : IEntityTypeConfiguration<Review>
{
    public void Configure(EntityTypeBuilder<Review> builder)
    {
        builder.HasKey(r => r.Id);

        // WHY: enforces the business rule "one review per user per movie"
        // at the database level — application checks alone can race.
        builder.HasIndex(r => new { r.UserId, r.MovieId }).IsUnique();

        builder.Property(r => r.Body).HasMaxLength(4000);

        // WHY: value object stored as a simple column via conversion.
        builder.Property(r => r.Rating)
               .HasConversion(v => v.Value, v => Rating.From(v));

        builder.HasOne<Movie>()
               .WithMany()
               .HasForeignKey(r => r.MovieId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(r => r.UserId)
               .OnDelete(DeleteBehavior.Restrict); // WHY: deleting a user must not silently erase review history
    }
}

// Read side in a query handler — projection, no tracking:
var movie = await db.Movies
    .AsNoTracking()
    .Where(m => m.Id == request.MovieId)
    .Select(m => new MovieDetailsDto(
        m.Id, m.Title,
        m.Reviews.Average(r => (double?)r.Rating), // WHY: computed in SQL, not in memory
        m.Reviews.Count))
    .FirstOrDefaultAsync(ct);
```

## Notes for Cinora Entities
- `ReviewLike`: composite key `(ReviewId, UserId)` — one like per user.
- `Watchlist`: unique index on `(UserId, MovieId)`.
- `Notification`, `Device`, `AIRecommendationHistory`: index the `UserId` FK plus common filter columns (e.g., `IsRead`).
- Migrations workflow lives in `.claude/skills/ef-core-migrations/SKILL.md`.

## Common Mistakes
| Mistake | Fix |
|---|---|
| Tracking entities for read-only pages | `AsNoTracking()` + projection |
| `Include` chains to build DTOs | Project with `Select`; EF translates to one efficient query |
| Generic `IRepository<T>` over everything | Direct DbContext in handlers; repository only when it earns its keep |
| Cascade cycles on User self-references (Friend) | `DeleteBehavior.Restrict` on at least one side |
| Data annotations mixed with Fluent config | Fluent API in configurations is the single source of truth |
| Sync calls (`ToList`, `SaveChanges`) | Async everywhere with `CancellationToken` |
