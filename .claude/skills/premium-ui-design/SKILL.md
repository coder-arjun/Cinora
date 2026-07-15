---
name: premium-ui-design
description: Use when styling Cinora screens or components — choosing surface colors, elevation, glassmorphism, typography, spacing, or treating poster and backdrop imagery so the UI reads as a premium streaming platform.
---

# Premium UI Design

## Overview
Cinora's design language is dark-first and restrained: near-black layered surfaces, exactly one accent color, glass used sparingly over imagery, and confident typography with generous breathing room.

## Quick Reference
| Element | Rule |
|---|---|
| Palette | Near-black base with a subtle hue, 2–3 elevation steps, one gold accent |
| Elevation | Lighter surface step + soft shadow — not heavier borders |
| Glassmorphism | backdrop-blur + translucent surface + 1px light border + subtle shadow; only on overlays/cards sitting over imagery |
| Headings | Display face, tight tracking (≈ -0.02em), tight leading |
| Body | ≥16px, line-height ~1.6, muted ink (`white/70`), pure white reserved for emphasis |
| Spacing | 4px scale; generous section padding — density is the enemy of luxury |
| Posters | Locked 2:3 aspect ratio, rounded corners, subtle hover lift |
| Backdrops | Always a gradient scrim between image and text |

## Pattern
```css
/* Glass overlay card — for content floating over backdrop imagery. */
.card-glass {
  background: color-mix(in oklab, var(--color-surface-1) 60%, transparent);
  backdrop-filter: blur(var(--blur-glass)); /* WHY: blur only reads as glass when imagery shows through — pointless on flat backgrounds */
  border: 1px solid rgb(255 255 255 / 0.10); /* WHY: the 1px light border fakes an edge catching light — the signature glass cue */
  border-radius: var(--radius-card);
  box-shadow: 0 8px 32px rgb(0 0 0 / 0.35);  /* WHY: soft, large shadow lifts the pane off the backdrop */
}

/* Backdrop scrim — guarantees text contrast over any movie art. */
.backdrop-hero { position: relative; }
.backdrop-hero::after {
  content: "";
  position: absolute;
  inset: 0;
  background: linear-gradient(
    to top,
    var(--color-surface-0) 0%,   /* WHY: bottom fades into the page background so the hero bleeds seamlessly into content */
    rgb(0 0 0 / 0.55) 45%,
    transparent 100%
  );
}
```

## Common Mistakes
| Mistake | Fix |
|---|---|
| Pure `#000` background | Near-black with a hint of hue (`--color-surface-0`) — pure black feels flat and crushes shadows |
| Glass applied to every card | Reserve for overlays above imagery; flat pages get solid surface steps |
| Second and third accent colors creeping in | One accent; hierarchy comes from type and elevation |
| Text laid directly on posters/backdrops | Gradient scrim first, always |
| Hairline gray borders everywhere | Separate layers with surface steps and shadow instead |
| Tight body leading and cramped sections | Line-height ~1.6, oversized section padding |

Define every value above as a token — see `.claude/skills/tailwind-v4/SKILL.md`. Motion rules: see `.claude/skills/ui-animations/SKILL.md`. Contrast requirements: see `.claude/skills/responsive-accessibility/SKILL.md`.
