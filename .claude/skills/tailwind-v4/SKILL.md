---
name: tailwind-v4
description: Use when configuring or troubleshooting Tailwind CSS v4 in Cinora — theme tokens, @theme setup, missing utilities, dark-theme design variables, or wiring the Tailwind build into the MVC dev and publish pipeline.
---

# Tailwind CSS v4

## Overview
Tailwind v4 is CSS-first: configuration lives in the main stylesheet via `@theme`, which generates both utility classes and CSS custom properties — there is no `tailwind.config.js` by default.

## Quick Reference
| Task | Approach |
|---|---|
| Entry point | `@import "tailwindcss";` at the top of `Styles/app.css` |
| Design tokens | `@theme { --color-*, --radius-*, --blur-*, --font-* }` |
| Dev build | `npx @tailwindcss/cli -i Styles/app.css -o wwwroot/css/app.css --watch` alongside `dotnet watch` |
| Publish build | Same CLI with `--minify`, run as a pre-publish step (npm script or MSBuild target) |
| Repeated component shells | `@layer components` with sparing `@apply` |
| One-off custom utility | `@utility` directive |
| Tokens in handwritten CSS | `var(--color-accent)` — same variables the utilities use |

## Pattern
```css
/* Styles/app.css — the single source of design truth for Cinora */
@import "tailwindcss";

@theme {
  /* WHY: each @theme value becomes a utility (bg-surface-1, rounded-card,
     backdrop-blur-glass) AND a CSS variable usable in plain CSS. */
  --color-surface-0: oklch(0.13 0.01 270);  /* near-black page background */
  --color-surface-1: oklch(0.17 0.012 270); /* elevated cards */
  --color-surface-2: oklch(0.21 0.014 270); /* modals, popovers */
  --color-accent: oklch(0.75 0.16 75);      /* the single gold accent */
  --radius-card: 1rem;
  --blur-glass: 20px;
  --font-display: "Fraunces", ui-serif, serif;
}

@layer components {
  /* WHY: @apply only for genuinely repeated shells like this glass card —
     everything else stays as utilities directly in the Razor markup. */
  .card-glass {
    @apply rounded-card border border-white/10 bg-surface-1/60
           backdrop-blur-glass shadow-lg shadow-black/30;
  }
}
```

## Common Mistakes
| Mistake | Fix |
|---|---|
| Creating `tailwind.config.js` out of v3 habit | Configure in CSS with `@theme`; a JS config is opt-in via `@config` only |
| v3 directives `@tailwind base/components/utilities` | Replace with a single `@import "tailwindcss";` |
| Hardcoded hex colors scattered in markup | Define once in `@theme`, consume as utilities or `var(...)` |
| `@apply` used to rebuild semantic CSS everywhere | Utilities in markup first; `@apply` only for real repeated components |
| Shipping the unminified dev CSS | Add the `--minify` build before `dotnet publish` |
| Editing `wwwroot/css/app.css` by hand | It is build output; edit `Styles/app.css` |

Token values and usage rules for the dark luxury theme: see `.claude/skills/premium-ui-design/SKILL.md`. Motion-related tokens: see `.claude/skills/ui-animations/SKILL.md`.
