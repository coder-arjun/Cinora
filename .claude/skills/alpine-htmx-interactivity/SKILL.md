---
name: alpine-htmx-interactivity
description: Use when adding client interactivity to Cinora pages — deciding between Alpine and HTMX, wiring hx-post with anti-forgery tokens, building dropdowns, modals, star ratings, like buttons, or infinite feed scroll.
---

# Alpine + HTMX Interactivity

## Overview
Alpine owns purely local UI state; HTMX owns server round-trips that swap Razor partials. Every element is controlled by exactly one of them — never both.

## Quick Reference
| Need | Tool |
|---|---|
| Dropdown, modal, tabs, star-rating input | Alpine `x-data` — no server needed |
| Like button, comment form, follow toggle | HTMX `hx-post` → partial swap |
| Infinite feed scroll | `hx-get` with `hx-trigger="revealed"` on a sentinel row |
| Anti-forgery | Hidden `@Html.AntiForgeryToken()` field (forms) or `hx-headers` with `RequestVerificationToken` (non-form) |
| Server responses | Razor partials — see `.claude/skills/razor-views/SKILL.md` |
| No-JS fallback | Real `<form>`/`<a>` underneath; HTMX only enhances |

## Pattern
```html
<!-- Like button: a real form (works with JS disabled); HTMX upgrades it in place. -->
<form method="post" asp-action="Like" asp-route-id="@Model.ReviewId"
      hx-post="/reviews/@Model.ReviewId/like"
      hx-target="this" hx-swap="outerHTML">
  @Html.AntiForgeryToken()
  <!-- WHY: htmx submits form fields, so the hidden anti-forgery input is
       posted automatically — no header wiring needed inside a form. -->
  <button type="submit" class="btn-ghost">♥ @Model.LikeCount</button>
</form>

<!-- Star rating: local state until the form submits — Alpine territory. -->
<div x-data="{ rating: @Model.MyRating, hover: 0 }" class="flex gap-1">
  <template x-for="star in 5" :key="star">
    <button type="button"
            x-on:click="rating = star"
            x-on:mouseenter="hover = star" x-on:mouseleave="hover = 0"
            :class="(hover || rating) >= star ? 'text-accent' : 'text-white/30'"
            :aria-pressed="(rating >= star).toString()">★</button>
  </template>
  <input type="hidden" name="Rating" :value="rating">
  <!-- WHY: the hidden input travels with the surrounding form/HTMX post,
       so Alpine never talks to the server itself. -->
</div>
```

## Common Mistakes
| Mistake | Fix |
|---|---|
| HTMX swap wipes Alpine state inside the target | Keep `x-data` roots inside the swapped fragment (re-initialized on swap) or outside it entirely; `hx-preserve` for exceptions |
| Alpine `@click` in `.cshtml` breaks Razor | Escape as `@@click` or use `x-on:click` |
| POST returns 400 on HTMX requests | Anti-forgery token missing — include the hidden field or `hx-headers` |
| Modal/dropdown via HTMX round-trip | Pure client state — use Alpine |
| Hand-rolled `fetch` for form posts | HTMX attributes on the existing form |
| Feature only works with JS enabled | Start from a working form/link, then enhance |

Feed pagination endpoints should use keyset cursors — see `.claude/skills/dotnet-performance/SKILL.md`.
