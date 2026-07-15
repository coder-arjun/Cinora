---
name: cqrs-mediatr
description: Use when creating commands, queries, or handlers, adding MediatR pipeline behaviors, naming requests, or deciding whether a feature needs CQRS at all in Cinora's Application layer.
---

# CQRS with MediatR

## Overview
Every use case is one request plus one handler: commands change state and return little; queries return data and change nothing. Cross-cutting concerns run once in pipeline behaviors, not in every handler.

## Quick Reference
| Task | Approach |
|---|---|
| Command naming | Verb + noun + `Command`: `CreateReviewCommand`, `AcceptFriendRequestCommand` |
| Query naming | `Get` + noun + `Query`: `GetMovieDetailsQuery`, `GetWatchlistQuery` |
| Request shape | `public record CreateReviewCommand(...) : IRequest<int>` |
| Handler location | `Cinora.Application/Features/<Feature>/` — request, handler, validator, DTO together |
| Validation | `ValidationBehavior` before handlers; see `.claude/skills/fluent-validation/SKILL.md` |
| Logging/timing | `LoggingBehavior`, `PerformanceBehavior` (warn if a request exceeds ~500 ms) |
| Registration | `AddMediatR(cfg => cfg.RegisterServicesFromAssembly(...))` + `AddBehavior` in pipeline order |
| Reads | `AsNoTracking` projections; see `.claude/skills/ef-core-data-access/SKILL.md` |

## Pattern
```csharp
// Cinora.Application/Features/Reviews/CreateReview.cs
public record CreateReviewCommand(int MovieId, int RatingValue, string? Body)
    : IRequest<int>; // WHY: returns only the new Id — commands don't return read models

public class CreateReviewCommandHandler(
    IAppDbContext db,
    ICurrentUser currentUser) : IRequestHandler<CreateReviewCommand, int>
{
    public async Task<int> Handle(CreateReviewCommand request, CancellationToken ct)
    {
        // WHY: no validation here — the ValidationBehavior already ran,
        // so the handler expresses only the use case itself.
        var review = Review.Create(
            currentUser.Id,
            request.MovieId,
            Rating.From(request.RatingValue),
            request.Body);

        db.Reviews.Add(review);
        await db.SaveChangesAsync(ct);
        return review.Id;
    }
}

// Pipeline behavior — runs for every request, written once:
public class PerformanceBehavior<TRequest, TResponse>(
    ILogger<TRequest> logger) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var response = await next(ct);
        if (sw.ElapsedMilliseconds > 500)
            logger.LogWarning("Slow request {RequestName} took {Elapsed}ms",
                typeof(TRequest).Name, sw.ElapsedMilliseconds);
        return response;
    }
}
```

## When CQRS Is Overkill
- Single-table CRUD with no business rules (e.g., an admin genre editor) — a thin service or direct handler is fine; don't manufacture ceremony.
- Don't split into separate read/write databases — Cinora uses one SQL Server; the separation is in code shape only.
- One handler calling another via `ISender` is a smell — extract shared logic into a domain or application service.

## Common Mistakes
| Mistake | Fix |
|---|---|
| Commands returning full DTOs "to save a round trip" | Return the Id; let the client query (or return the rendered partial from the controller) |
| Queries calling `SaveChangesAsync` | Queries never mutate; move side effects to a command |
| Handler-to-handler `ISender` calls | Extract a shared service; keep the pipeline one level deep |
| Fat generic `IRequestHandler` base classes | Handlers stay small and independent |
| Business rules in behaviors | Behaviors are cross-cutting only; rules belong in Domain |
