---
description: Agentic loop — UI/UX review until no Critical/High findings remain
---

# UI Review Loop

Run an iterative UI/UX review of Cinora until it is clean. Run this loop whenever a milestone changed views, styles, or client-side behavior.

## Scope
Design-brief adherence (luxury dark theme, glassmorphism used deliberately — not on everything, premium typography, consistent spacing/elevation tokens), responsiveness (mobile-first, no horizontal scroll, touch targets ≥ 44px), accessibility (WCAG 2.2 AA contrast on dark surfaces, keyboard navigation, focus-visible, semantic HTML, aria-live for HTMX swaps, `prefers-reduced-motion` honored), animation quality (transform/opacity only, purposeful durations, no jank), loading states (skeletons over spinners), empty states and error states designed, app-flow coherence (Landing → Login → Home → Search → Movie Details → Review → Feed → Notifications → Profile).

## Loop Protocol
1. Dispatch **ux-agent** (review mode, no edits) across the changed UI. Where a browser is available, verify rendered pages rather than only reading markup. Findings rated **Critical / High / Medium / Low** with file (and screen/viewport) references and concrete fixes.
2. Critical/High: delegate fixes to **frontend-agent**. Medium: fix if cheap, else `REVIEW_BACKLOG.md`. Low: record.
3. Verify the fix visually or by re-inspecting the markup/styles; build must pass.
4. Re-run on affected screens. Exit at **zero Critical/High** or after **3 iterations** (report honestly).

## Report
Findings table per iteration (severity, screen, status), fixes, deferred items, final verdict.

## Hard Rules
- NEVER run `git commit` or `git push`.
- Accessibility findings at WCAG AA level are High by default — they are not polish items.
