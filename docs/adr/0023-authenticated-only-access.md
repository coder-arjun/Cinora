# ADR 0023 — Authenticated-Only Access (Cinora is Login-Only)

- **Status:** Accepted
- **Date:** 2026-07-11
- **Phase:** Post-Phase-6 — Friends & Social overhaul (Milestone B4)
- **Deciders:** the user (product decision, 2026-07-11), security-agent (audit), backend-agent
  (implementation), architecture-agent (ratify)

## Context

Phase 3 (ADR 0009, `phase-3-social-design.md` §7/§10) deliberately left a set of **public, anonymous** content
reads so an unauthenticated visitor could browse: the whole `DiscoveryController` (`/discover` rails, search,
title details) and the **public title-reviews list** it hosts, plus the `CommentsController.List` comment
thread. Marketing/entry surfaces (the landing page, the `/tour`, the auth pages) were also public.

The user observed that *"even without signing in I am able to access the movies through the discover page"* and
decided (2026-07-11) that **Cinora should require login for everything** — the app's content is not for
anonymous visitors. This reverses the Phase-3 public-browse stance.

## Decision

**Make the app login-only: remove every `[AllowAnonymous]` from CONTENT surfaces so they inherit the global
fail-closed fallback policy (an anonymous request is redirected to `/account/login`). Keep public ONLY a small,
explicit allowlist — the marketing entry, the auth flow, the PWA/offline + token-gated media infrastructure,
the health probe, and static assets.**

### 1. Locked behind login (removed `[AllowAnonymous]`)

- **`DiscoveryController`** (class-level attribute removed) — `/discover` rails, `/discover/rail`,
  `/discover/search` + results, `/discover/title/{media}/{tmdbId}` details, and the public reviews list it
  renders. All now require authentication.
- **`CommentsController.List`** (per-action attribute removed) — the review comment thread now requires
  authentication.

No `[Authorize]` needed on these — the **global fail-closed fallback policy** (Phase 1) requires auth for any
endpoint lacking an explicit `[AllowAnonymous]`, so simply removing the opt-out locks them.

### 2. The public allowlist (stays anonymous — justified)

| Surface | Why it stays public |
|---|---|
| `HomeController.Landing` (`GET /`) | the marketing entry; an anonymous visitor needs somewhere to land and choose "Sign in" (authenticated users are redirected to `/home`). |
| `AccountController` (register, login, forgot/reset-password, external-login + callback, denied) | the auth flow itself — you cannot sign in from behind a login wall. |
| `TourController` (`/tour`) | marketing/feature walkthrough — part of the pre-signup entry, not app content. |
| `PwaController` (`/offline`, manifest, sw) | the service worker + offline fallback must be reachable without a session, especially offline. |
| `MediaController` (avatar serving) | gated by an **HMAC-signed, bucketed-expiry token** (ADR 0013), which is the real access control; auth would add nothing and could break image loads. |
| `/health` | the anonymous DB-connectivity probe (ADR 0021). |
| `DiagnosticsController` | Development/Testing-only — never mapped in Production; left as-is. |
| static assets (`/dist`, `/icons`, …) | non-secret, cache-immutable build output. |

### 3. Consequences of the removal

- The Details page's anonymous "Sign in to review" branch and any anonymous output-cache variant become dead
  and may be pruned (cosmetic; they never render under auth).
- Existing integration tests that asserted **anonymous** browse (Discovery/Search/Details/reviews/comments) are
  converted to run **authenticated** (their content/CSP/pagination assertions still hold), and a new
  `AnonymousLockdownTests` asserts anonymous content routes → 302-login while the allowlist stays 200.

## Consequences

**Positive**
- No anonymous attack/enumeration surface on content; the friends/search/blocking privacy model (ADR 0022) is
  not undermined by a public browse path.
- A single, explicit allowlist makes "what is public" auditable — a security-review grep for `[AllowAnonymous]`
  now returns only justified survivors.

**Negative / accepted**
- **No public/SEO/deep-link sharing of content** — a shared `/discover/title/...` link now bounces an
  anonymous recipient to login. Accepted: Cinora is a private social app for its members, per the user's
  decision.
- The marketing landing/tour still expose the product's existence (intended — that is the signup funnel).
- Avatar serving stays token-gated rather than auth-gated (unchanged from ADR 0013) — the token is the control.

## Alternatives considered

1. **Keep public browse, require login only for social actions.** Rejected — the user explicitly wants
   login-only; public content was the reported problem.
2. **Dump anonymous visitors straight onto the login form (no public landing).** Rejected — a public
   landing/tour is the signup funnel; only the auth + marketing entry stay public.
3. **Lock the token-gated avatar `MediaController` behind `[Authorize]` too.** Rejected — the HMAC token
   already gates it; adding auth is redundant and risks breaking image/SW fetches.

## Related

- Design: `docs/architecture/friends-social-overhaul-design.md` §8.
- Reverses part of `docs/architecture/phase-3-social-design.md` §7/§10 (public reads).
- ADR 0009 (fail-closed authZ + `[AllowAnonymous]` opt-out — the mechanism), ADR 0013 (token-gated media),
  ADR 0021 (`/health` probe), ADR 0022 (blocking privacy this lockdown complements).
- Skill: `.claude/skills/security-hardening/SKILL.md`.

---

_Design-only ADR authored 2026-07-11 against the shipped Milestone-B4 code (verified: `[AllowAnonymous]` removed
from `DiscoveryController` (class) and `CommentsController.List`; `AccountController`/`HomeController.Landing`/
`TourController`/`PwaController`/`MediaController`/`DiagnosticsController` allowlist retained; new
`AnonymousLockdownTests` asserts content → 302-login and allowlist → 200; the anonymous-browse Discovery/reviews/
comments tests converted to authenticated). No git performed; the human commits._
