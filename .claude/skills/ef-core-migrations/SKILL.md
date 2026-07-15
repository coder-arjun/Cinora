---
name: ef-core-migrations
description: Use when adding, removing, or applying EF Core migrations, generating deployment SQL scripts, seeding data, or fixing design-time DbContext errors in Cinora — including "unable to create DbContext" failures.
---

# EF Core Migrations

## Overview
Migrations are code artifacts: named intentionally, reviewed like any other code, and applied to real environments via idempotent SQL scripts — never `EnsureCreated`.

## Quick Reference
| Task | Approach |
|---|---|
| Add | `dotnet ef migrations add AddReviewUniqueIndex -p Cinora.Infrastructure -s Cinora.Web` |
| Undo last (unapplied) | `dotnet ef migrations remove -p Cinora.Infrastructure -s Cinora.Web` |
| Apply locally | `dotnet ef database update -p Cinora.Infrastructure -s Cinora.Web` |
| Deployment script | `dotnet ef migrations script --idempotent -o migrate.sql -p Cinora.Infrastructure -s Cinora.Web` |
| Naming | PascalCase, intent-revealing: `InitialCreate`, `AddWatchlist`, `MakeReviewBodyNullable` |
| Reference/static data (Genres) | `HasData` in entity configuration — deterministic, keys hardcoded |
| Environment/dynamic data (admin user, dev fixtures) | Runtime seeder invoked from Program.cs in Development |
| Design-time context | `IDesignTimeDbContextFactory<AppDbContext>` in Infrastructure |

## Pattern
```csharp
// Cinora.Infrastructure/Persistence/AppDbContextFactory.cs
// WHY: dotnet-ef runs outside the app host — no DI, no Program.cs —
// so it needs an explicit way to construct the context at design time.
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddUserSecrets<AppDbContextFactory>(optional: true) // WHY: keeps the connection string out of source control
            .AddEnvironmentVariables()
            .Build();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(
                config.GetConnectionString("CinoraDb"),
                sql => sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
            .Options;

        return new AppDbContext(options);
    }
}
```

Static seed data belongs in configuration so it versions with the model:

```csharp
builder.HasData(
    new Genre { Id = 1, Name = "Action" },
    new Genre { Id = 2, Name = "Drama" }); // WHY: fixed IDs make HasData diffs stable across migrations
```

## SQL Server Specifics
- `--idempotent` scripts wrap each migration in `IF NOT EXISTS` checks against `__EFMigrationsHistory` — safe to run repeatedly in CI/CD.
- Watch for cascade-cycle errors on self-referencing `Friend`; fix in model config (`Restrict`), not by hand-editing the migration. See `.claude/skills/ef-core-data-access/SKILL.md`.
- Review generated migrations for destructive operations (column drops, type narrowing) before merging.

## Common Mistakes
| Mistake | Fix |
|---|---|
| `db.Database.Migrate()` on startup in production | Generate idempotent script; apply in the deployment pipeline |
| `EnsureCreated()` anywhere migrations exist | Never mix them; `EnsureCreated` bypasses migration history |
| Vague names (`Update1`, `Fix`) | Name the intent: `AddNotificationReadIndex` |
| Editing an already-applied migration | Add a new migration that corrects it |
| `HasData` with generated/random IDs | Hardcode keys so snapshots stay deterministic |
| Missing `-p`/`-s` flags | Migrations live in Infrastructure; startup project is Web |
