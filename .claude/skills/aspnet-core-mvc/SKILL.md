---
name: aspnet-core-mvc
description: Use when writing or reviewing controllers, actions, routing, filters, model binding, Program.cs wiring, or HTMX partial responses in Cinora.Web — including questions about where request-handling logic should live.
---

# ASP.NET Core MVC (.NET 10)

## Overview
Controllers are thin translators: bind the request, send a MediatR request, shape the response. All behavior lives in Application handlers.

## Quick Reference
| Task | Approach |
|---|---|
| Program.cs | Minimal hosting; `AddControllersWithViews()`; layer extensions `AddApplication()`, `AddInfrastructure()` |
| Controller size | No business logic, no DbContext — inject `ISender`, delegate |
| Routing | Attribute routing on controllers (`[Route("movies")]`); conventional route only as fallback |
| HTMX request | Return `PartialView(...)` when `Request.Headers["HX-Request"]` is present; full view otherwise |
| Validation errors | FluentValidation via pipeline; re-render partial with `ModelState` for forms |
| API-style errors | `Results.Problem(...)` / ProblemDetails; see `.claude/skills/error-handling-logging/SKILL.md` |
| Cross-cutting per-action | Action filters (e.g., `[ValidateAntiForgeryToken]`, custom audit filter) |
| Model binding | Bind to dedicated request models/commands, never to Domain entities |

## Pattern
```csharp
[Route("reviews")]
public class ReviewsController(ISender sender) : Controller // WHY: primary ctor, ISender only — thin by construction
{
    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateReviewCommand command, CancellationToken ct)
    {
        // WHY: no try/catch here — global IExceptionHandler and the
        // validation pipeline behavior own failure paths.
        var result = await sender.Send(command, ct);

        // WHY: HTMX swaps a fragment; a full redirect would break the swap.
        if (Request.Headers.ContainsKey("HX-Request"))
            return PartialView("_ReviewCard", result);

        return RedirectToAction("Details", "Movies", new { id = command.MovieId });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
        => View(await sender.Send(new GetReviewDetailsQuery(id), ct));
}
```

Program.cs order matters: `UseExceptionHandler()` first, then static files, routing, auth (`UseAuthentication` before `UseAuthorization`), then `MapControllers()` / `MapDefaultControllerRoute()`.

## Model Binding Notes
- Accept commands/queries directly as action parameters when their shape matches the form; use `[FromRoute]`/`[FromQuery]` explicitly when mixing sources.
- Always accept and forward `CancellationToken` — MVC binds it from `HttpContext.RequestAborted`.

## Common Mistakes
| Mistake | Fix |
|---|---|
| Business rules or LINQ queries in actions | Move to a handler; see `.claude/skills/cqrs-mediatr/SKILL.md` |
| Binding forms straight to Domain entities | Bind to commands/view models; entities are constructed in handlers |
| Returning full views to HTMX requests | Check `HX-Request` header and return `PartialView` |
| try/catch in every action | One global `IExceptionHandler` + ProblemDetails |
| Forgetting `[ValidateAntiForgeryToken]` on POSTs | Required for cookie-authenticated form posts, including HTMX |
| Ignoring `CancellationToken` | Thread it from action to handler to EF Core |
