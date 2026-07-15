---
name: fluent-validation
description: Use when writing validators for commands or queries, wiring FluentValidation into DI or the MediatR pipeline, adding async rules like uniqueness checks, or mapping validation failures to user-visible errors in Cinora.
---

# FluentValidation

## Overview
One validator class per command/query, executed automatically by a MediatR pipeline behavior so invalid requests never reach a handler.

## Quick Reference
| Task | Approach |
|---|---|
| Validator per request | `CreateReviewCommandValidator : AbstractValidator<CreateReviewCommand>` next to the command |
| DI registration | `services.AddValidatorsFromAssembly(typeof(...).Assembly)` in Application's `AddApplication()` |
| Pipeline hookup | `ValidationBehavior<TRequest,TResponse>` registered before other behaviors — fail fast |
| Async rules | `MustAsync` for DB checks (uniqueness, existence) |
| Error surface (MVC forms) | Catch `ValidationException`, copy failures into `ModelState`, re-render the partial |
| Error surface (JSON/HTMX API) | Map to `ValidationProblemDetails` (400); see `.claude/skills/error-handling-logging/SKILL.md` |
| Message style | User-readable, no internals: "You have already reviewed this movie." |

## Pattern
```csharp
// Cinora.Application/Features/Reviews/CreateReviewCommandValidator.cs
public class CreateReviewCommandValidator : AbstractValidator<CreateReviewCommand>
{
    private readonly IAppDbContext _db;

    public CreateReviewCommandValidator(IAppDbContext db, ICurrentUser currentUser)
    {
        _db = db;

        // WHY: the canonical Cinora rating rule — 1 to 10 inclusive.
        RuleFor(x => x.RatingValue)
            .InclusiveBetween(1, 10)
            .WithMessage("Rating must be between 1 and 10.");

        RuleFor(x => x.Body)
            .MaximumLength(4000)
            .When(x => x.Body is not null);

        // WHY: async uniqueness check gives a friendly error up front;
        // the unique DB index (UserId, MovieId) remains the race-proof backstop.
        RuleFor(x => x.MovieId)
            .MustAsync(async (movieId, ct) =>
                !await _db.Reviews.AnyAsync(
                    r => r.MovieId == movieId && r.UserId == currentUser.Id, ct))
            .WithMessage("You have already reviewed this movie.");
    }
}

// The pipeline behavior that runs all validators before any handler:
public class ValidationBehavior<TRequest, TResponse>(
    IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (validators.Any())
        {
            var context = new ValidationContext<TRequest>(request);
            var results = await Task.WhenAll(
                validators.Select(v => v.ValidateAsync(context, ct)));
            var failures = results.SelectMany(r => r.Errors).Where(f => f is not null).ToList();
            if (failures.Count != 0)
                throw new ValidationException(failures); // WHY: one throw site; middleware maps it to 400
        }
        return await next(ct);
    }
}
```

## Guidance
- Validators check request shape and cheap preconditions; deep invariants stay in Domain entities (see `.claude/skills/clean-architecture-dotnet/SKILL.md`).
- Property names in failures map to form fields — keep command property names aligned with view model inputs so errors land next to the right input.

## Common Mistakes
| Mistake | Fix |
|---|---|
| Validating again inside the handler | Trust the pipeline; the handler assumes a valid request |
| Business authorization in validators ("is this my review?") | That's an authorization concern — separate behavior or handler check |
| Relying on the async uniqueness rule alone | Keep the unique index; two requests can pass validation concurrently |
| Blocking calls (`.Result`) in `Must` | Use `MustAsync` |
| Technical messages leaking to users ("FK violation") | Write human messages with `WithMessage` |
