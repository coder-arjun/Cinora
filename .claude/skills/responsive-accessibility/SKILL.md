---
name: responsive-accessibility
description: Use when building or reviewing Cinora UI for mobile-first layout or accessibility — Tailwind breakpoint decisions, dark-theme contrast checks, keyboard navigation for modals and menus, star-rating inputs, focus styling, or screen-reader behavior of HTMX-swapped content.
---

# Responsive Accessibility

## Overview
Cinora is mobile-first and dark by default: base styles are unprefixed (mobile), Tailwind breakpoints layer upward, and WCAG 2.2 AA is a hard requirement measured against near-black surfaces — luxury never excuses invisible focus or grey-on-grey text.

## Quick Reference
| Task | Approach |
|---|---|
| Breakpoints | Unprefixed = mobile; enhance with `sm:` `md:` `lg:` — never desktop-first |
| Text contrast | ≥ 4.5:1 against the actual surface (e.g. `#e5e5e5` on `#0a0a0a`); large text ≥ 3:1 |
| Non-text contrast | Icons, borders, focus rings ≥ 3:1 (WCAG 1.4.11) |
| Focus style | Gold `focus-visible` ring (`ring-2 ring-amber-400 ring-offset-2 ring-offset-neutral-950`); never bare `outline-none` |
| Modals/menus | Focus trap, `Escape` closes, focus returns to the trigger element |
| Star rating | Real `<input type="radio">` in a fieldset — never clickable divs |
| HTMX swaps | Announce results via an `aria-live="polite"` region outside the swap target |
| Testing | Tab through every page; NVDA or VoiceOver pass on the review form |

## Pattern
Accessible 1–10 rating input — semantic HTML gives keyboard and screen-reader support for free:

```html
<fieldset class="flex gap-1">
  <legend class="sr-only">Rate this movie from 1 to 10</legend>
  <!-- WHY: native radios provide arrow-key navigation, grouping, and form
       posting without any JavaScript -->
  <label class="cursor-pointer">
    <input type="radio" name="Rating" value="1" class="sr-only peer" />
    <!-- WHY: peer-focus-visible keeps the luxury aesthetic while making
         keyboard focus unmistakable at 3:1+ contrast -->
    <svg class="size-7 rounded text-neutral-600 peer-checked:text-amber-400
                peer-focus-visible:ring-2 peer-focus-visible:ring-amber-400"
         aria-hidden="true"><use href="#star" /></svg>
    <span class="sr-only">1 star</span>
  </label>
  <!-- repeat labels for values 2–10 -->
</fieldset>
<!-- WHY: lives OUTSIDE any HTMX swap target so it survives swaps and announces -->
<div id="rating-status" aria-live="polite" class="sr-only"></div>
```

Style tokens and breakpoints follow `.claude/skills/tailwind-v4/SKILL.md`; interactive behavior (focus traps for Alpine dropdowns, HTMX swap events) pairs with `.claude/skills/alpine-htmx-interactivity/SKILL.md`. Verify flows end-to-end per `.claude/skills/playwright-e2e/SKILL.md`.

## Common Mistakes
| Mistake | Fix |
|---|---|
| `outline-none` with no replacement | Style `focus-visible` with a visible ≥ 3:1 ring |
| Guessing contrast on dark surfaces | Check every pair against the real surface token with a contrast checker |
| Clickable `<div>` buttons and menus | Use `<button>`, `<nav>`, `<dialog>` — semantic HTML over div soup |
| `aria-live` inside the HTMX target | The region is replaced and never announces; keep it outside the swap |
| Modal without a focus trap | Trap Tab, close on Escape, restore focus to the trigger |
| Desktop-first overrides | Start mobile, add breakpoints upward only |
