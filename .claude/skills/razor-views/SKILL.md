---
name: razor-views
description: Use when creating or restructuring Razor views, layouts, partials, View Components, or tag helpers in Cinora — including HTMX partial endpoints, view model design, encoding concerns, or _ViewImports organization.
---

# Razor Views

## Overview
Views render strongly-typed view models — never EF entities — through a clear layout hierarchy, using partials for markup reuse and View Components only when a fragment needs to fetch its own data.

## Quick Reference
| Task | Approach |
|---|---|
| Page shell | `_Layout.cshtml`; per-page assets via `@section Scripts` / `@section Head` |
| Markup reuse, data supplied by parent | Partial: `<partial name="_ReviewCard" model="item" />` |
| Self-contained UI with its own data | View Component (e.g., notification bell count in the header) |
| HTMX endpoint | Same controller action returns `PartialView` for `HX-Request`, full `View` otherwise |
| View models | One per view/partial, mapped in the MediatR handler |
| Output encoding | Razor `@` encodes by default; `Html.Raw` is a code smell |
| Shared directives | `_ViewImports.cshtml` per folder/area for `@using`, `@addTagHelper` |
| Links/forms | Tag helpers (`asp-action`, `asp-route-*`) over hardcoded URLs |

## Pattern
```csharp
// One action serves both full-page navigation and HTMX fragment swaps.
[HttpGet("movies/{id:int}/reviews")]
public async Task<IActionResult> Reviews(int id, string? cursor, CancellationToken ct)
{
    // WHY: the handler returns a view model, never an EF entity — views
    // can't trigger lazy loads or accidentally render fields like Email.
    ReviewListVm vm = await _mediator.Send(new GetMovieReviews(id, cursor), ct);

    // WHY: HTMX sends the HX-Request header; branching here keeps direct
    // navigation, bookmarks, and no-JS users working from the same URL.
    return Request.Headers.ContainsKey("HX-Request")
        ? PartialView("_ReviewList", vm)
        : View(vm);
}
```

## Common Mistakes
| Mistake | Fix |
|---|---|
| Passing entities or `IQueryable` to views | Project into a dedicated view model in the handler |
| View Component for a stateless snippet | Use a plain partial; components exist for self-sourced data |
| `Html.Raw(userContent)` | Keep Razor's default encoding; if rich text is truly required, sanitize server-side first |
| Business logic in `.cshtml` | Move to the handler or view model; views format, they don't decide |
| Repeating `@addTagHelper`/`@using` per view | Centralize in `_ViewImports.cshtml` |
| Separate duplicate actions for page vs fragment | One action branching on `HX-Request` |

For the client side of fragment swaps see `.claude/skills/alpine-htmx-interactivity/SKILL.md`; for where mapping and queries live see `.claude/skills/cqrs-mediatr/SKILL.md`.
