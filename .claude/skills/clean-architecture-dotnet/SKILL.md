---
name: clean-architecture-dotnet
description: Use when creating projects, deciding which layer a class belongs in, adding project references, or resolving "where does this code go" questions in the Cinora solution — Domain, Application, Infrastructure, or Web.
---

# Clean Architecture (.NET)

## Overview
Dependencies point inward: outer layers depend on inner layers, never the reverse. Domain knows nothing about EF Core, HTTP, or any framework.

## Quick Reference
| Task | Approach |
|---|---|
| Solution layout | `Cinora.Domain`, `Cinora.Application`, `Cinora.Infrastructure`, `Cinora.Web` |
| Reference direction | Web → Infrastructure + Application; Infrastructure → Application; Application → Domain |
| Entities, value objects, domain events | `Cinora.Domain` (zero NuGet dependencies ideally) |
| Commands, queries, handlers, validators, interfaces (`IEmailSender`, `ITmdbClient`) | `Cinora.Application` |
| DbContext, migrations, external API clients, interface implementations | `Cinora.Infrastructure` |
| Controllers, views, Program.cs, filters, middleware | `Cinora.Web` |
| Cross-cutting (validation, logging, caching) | MediatR pipeline behaviors in Application; middleware in Web |
| Wiring it together | Program.cs (composition root) calls `AddApplication()` / `AddInfrastructure()` extensions |

## Pattern
```csharp
// Cinora.Domain/Entities/Review.cs
// WHY: domain logic lives here, not in handlers or services —
// invariants are enforced at the source and impossible to bypass.
public class Review
{
    private Review() { } // WHY: EF Core needs it; forces callers through Create

    public int Id { get; private set; }
    public int UserId { get; private set; }
    public int MovieId { get; private set; }
    public Rating Rating { get; private set; } = null!; // value object, validates 1–10
    public string? Body { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    // WHY: factory centralizes invariants; a Review can never exist half-valid
    public static Review Create(int userId, int movieId, Rating rating, string? body) =>
        new()
        {
            UserId = userId,
            MovieId = movieId,
            Rating = rating,
            Body = body,
            CreatedAt = DateTimeOffset.UtcNow
        };

    public void Revise(Rating rating, string? body) // behavior, not property setters
    {
        Rating = rating;
        Body = body;
    }
}
```

## DDD-lite Guidance
- **Entities** have identity (`Review`, `User`, `Movie`); **value objects** are defined by their values (`Rating`, `EmailAddress`) and are immutable.
- Keep setters private; expose intent-revealing methods (`Revise`, `MarkAsRead`).
- Domain logic stays in Domain: if a handler contains an `if` about business rules, move it into the entity.
- Application defines interfaces for the outside world; Infrastructure implements them (Dependency Inversion).

## Common Mistakes
| Mistake | Fix |
|---|---|
| Domain referencing EF Core or MediatR | Domain stays dependency-free; map framework concerns in Infrastructure |
| Web referencing DbContext directly in controllers | Controllers send MediatR requests; see `.claude/skills/cqrs-mediatr/SKILL.md` |
| "Shared" or "Common" project that everything references | Put shared abstractions in Application, shared domain types in Domain |
| Anemic entities with public setters + logic in services | Move behavior onto entities; services orchestrate, entities decide |
| Infrastructure types leaking into Application signatures | Application depends only on its own interfaces and Domain types |
