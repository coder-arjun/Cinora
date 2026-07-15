---
name: ui-animations
description: Use when adding motion to Cinora — hover states, page or element transitions, loading states, staggered list reveals, or when animations feel janky, too slow, or ignore reduced-motion preferences.
---

# UI Animations

## Overview
Animate only `transform` and `opacity` so everything stays on the compositor, keep durations short and purposeful, and treat `prefers-reduced-motion` as a hard requirement, not a nice-to-have.

## Quick Reference
| Motion | Spec |
|---|---|
| Micro (hover, press, toggle) | 150–300ms, `ease-out` |
| Transitions (modals, panels, pages) | 300–500ms, custom cubic-bezier |
| Properties | `transform` + `opacity` only; never `top/left/width/height` |
| Page transitions | View Transitions API (`@view-transition`) — works across MVC navigations, no SPA required |
| List reveals | CSS `animation-delay` stagger, or Alpine `x-intersect` for scroll-triggered |
| Loading | Skeletons matching final layout, not spinners |
| Reduced motion | `@media (prefers-reduced-motion: reduce)` collapses everything to instant |

## Pattern
```css
/* Cross-document View Transitions: opt in once and full MVC page
   navigations get a smooth crossfade with zero JavaScript. */
@view-transition {
  navigation: auto;
}

/* Shared-element morph: give the SAME view-transition-name to the poster
   on the details hero and (via a click handler) to the one tapped feed card.
   WHY: names must be unique per page — never hardcode one onto a whole list. */
.poster-hero { view-transition-name: poster; }

/* Staggered card reveal — compositor-friendly: transform + opacity only. */
@keyframes rise-in {
  from { opacity: 0; transform: translateY(12px); }
  to   { opacity: 1; transform: translateY(0); }
}
.feed-card {
  /* WHY: 'backwards' holds the from-state during the delay so late cards don't flash in early */
  animation: rise-in 400ms cubic-bezier(0.16, 1, 0.3, 1) backwards;
  animation-delay: calc(var(--i) * 60ms); /* WHY: Razor loop sets style="--i:@i" — stagger without JS */
}

/* Non-negotiable: motion off for users who asked for it. */
@media (prefers-reduced-motion: reduce) {
  *, ::before, ::after {
    animation-duration: 0.01ms !important;
    transition-duration: 0.01ms !important;
  }
  @view-transition { navigation: none; }
}
```

## Common Mistakes
| Mistake | Fix |
|---|---|
| Animating `width`, `height`, `top`, `left` | `transform: scale()/translate()` — layout properties force reflow every frame |
| 700ms+ transitions "for elegance" | 150–500ms; slow is sluggish, not premium |
| `linear` or default `ease` on entrances | `ease-out` or an expressive bezier like `cubic-bezier(0.16, 1, 0.3, 1)` |
| Spinner while the feed loads | Skeleton cards matching the final layout — perceived speed wins |
| Skipping `prefers-reduced-motion` | The media query above ships with the first animation, not later |
| Everything on the page animates | Motion directs attention; reserve it for what changed |

Scroll-triggered reveals via `x-intersect`: see `.claude/skills/alpine-htmx-interactivity/SKILL.md`. Duration/easing as design tokens: see `.claude/skills/tailwind-v4/SKILL.md`. What deserves motion at all: see `.claude/skills/premium-ui-design/SKILL.md`.
