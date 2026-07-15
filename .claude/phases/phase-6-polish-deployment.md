# Phase 6 — Polish & Deployment

## Goal
Ship-ready: a true installable PWA with offline support and push notifications, performance and accessibility passes, final design polish, and a documented Azure deployment path.

## Prerequisites
Phase 5 exit criteria verified (all features functionally complete).

## In Scope
- PWA: manifest (icons, standalone, dark theme_color), service worker with per-resource caching strategies, offline fallback page, update flow (new-version prompt).
- Web push notifications: VAPID keys, in-context permission prompt, subscriptions stored on Device, server-side send for Notification events, 410 cleanup; per-user notification preferences honored.
- Performance pass: Lighthouse/Core Web Vitals on Home, Details, Feed; bundle audit; image loading policy (lazy, sized variants); OutputCache tuning.
- Accessibility pass: full keyboard walkthrough of the app flow, contrast audit on dark surfaces, aria-live on dynamic regions, reduced-motion audit.
- Animation/design polish: View Transitions between key pages, staggered rails, consistent empty/error/loading states.
- Deployment readiness (plans/scripts only): App Service + Azure SQL + Redis + Blob (+ Azure SignalR when scaling out), health checks, idempotent migration script, environment configuration, deployment-slot runbook.

## Out of Scope
Actually deploying. **Deployment is executed only when the user explicitly requests it. Nothing is ever committed or pushed by agents.**

## Agents
pwa-agent, frontend-agent, performance-agent, ux-agent, security-agent (final hardening sweep), devops-agent (runbook/scripts), testing-agent (E2E suite complete), documentation-agent (README, runbook, final ADRs).

## Skills
pwa-service-worker, web-push-notifications, dotnet-performance, ui-animations, responsive-accessibility, security-hardening, azure-deployment, playwright-e2e.

## Review Gates
All five loops, each run to a clean exit: /review-architecture, /review-code, /review-security, /review-performance, /review-ui.

## Exit Criteria
- App installs as a PWA; core browsing works offline with a designed fallback for uncached routes.
- Push notification round-trip verified end to end on a real browser.
- Playwright E2E suite covers login, search, review, watchlist, and notification flows — all green.
- Performance and accessibility audits meet targets (no Critical/High findings; WCAG AA).
- Deployment runbook and scripts reviewed by devops-agent and documented — not executed.
- `REVIEW_BACKLOG.md` triaged: every remaining item explicitly accepted or fixed.
