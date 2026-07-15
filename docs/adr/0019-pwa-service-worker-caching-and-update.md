# ADR 0019 — PWA Service-Worker Caching & Versioned Update, Keyed to the Content-Hashed Asset Manifest

- **Status:** Accepted
- **Date:** 2026-07-03
- **Phase:** 6 (Polish & Deployment), Milestone 6.1 (PWA foundation)
- **Deciders:** architecture-agent (pre-implementation design), pwa-agent + frontend-agent (consulted),
  orchestrator (ratify)

## Context

Phase 6 turns Cinora into a **true installable PWA** with offline support (exit criterion: "App installs as a
PWA; core browsing works offline with a designed fallback for uncached routes"). Cinora already has a bespoke
front-end asset pipeline: `build.mjs` (Tailwind v4 + esbuild) emits **content-hashed** `wwwroot/dist/app-*.css`
and `site-*.js` plus a `manifest.json` mapping logical → hashed URLs, resolved at runtime via `IAssetManifest`.
Two hard constraints bind the service-worker design:

1. **Stale-asset correctness across deploys.** A naive cache-everything service worker is the classic PWA
   footgun — after a deploy, clients keep serving old CSS/JS from the SW cache. The SW's cache lifetime must be
   tied to the **exact** content-hashed assets a build produced, so a new bundle invalidates the old cache.
2. **No CSP widening (`CLAUDE.md`, ADR 0011/0013 precedent).** The strict CSP is
   `default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' https://image.tmdb.org data:;
   connect-src 'self'; …` with **no** inline/eval and no third-party origins. The SW, the web app manifest, and
   the SW's runtime fetches must all fit inside that policy with **zero** directive changes.

A third concern is **privacy of offline caching**: Cinora has authenticated, personalized pages (`/home` feed,
`/notifications`, `/settings`, `/friends`, `/watchlist`). Caching those in a shared service-worker cache would
let a second user on a shared device read the first user's data offline.

## Decision

**Ship a single, versioned, hand-authored TypeScript service worker (`Scripts/sw.ts` → `wwwroot/sw.js`, root
scope, stable non-hashed name) bundled by the existing `build.mjs` as a second esbuild entry, whose cache
`VERSION` and app-shell precache list are injected at build time from the content-hashed `manifest.json`, with a
deliberate per-resource caching strategy, a privacy-scoped offline model, and a consent-gated versioned update
flow — and NO CSP change.**

### 1. Manifest + registration (installability, no CSP impact)

- `wwwroot/manifest.webmanifest` (`name`/`short_name` "Cinora", `start_url:"/"`, `scope:"/"`,
  `display:"standalone"`, dark `theme_color`/`background_color` from the `@theme` `--color-surface-0` token,
  192/512 + **maskable** PNG icons under `wwwroot/icons/`), linked from `_Layout.cshtml`. The manifest fetch is
  governed by `manifest-src`, which **falls back to `default-src 'self'`** — same-origin, admitted, **no CSP
  change**. Icons are same-origin (`img-src 'self'`).
- `Scripts/site.ts` registers `/sw.js` behind `if ("serviceWorker" in navigator)` (progressive enhancement).
  The SW is served from the **web root** so its scope is `/` (a `/js/sw.js` would only control `/js/`). The SW
  script itself is governed by `worker-src → child-src → script-src → default-src 'self'` — same-origin,
  admitted, **no CSP change**.

### 2. Cache keyed to the content-hashed manifest (the stale-asset fix)

`build.mjs` gains a **second esbuild entry** for `sw.ts` (output `wwwroot/sw.js`, **not** content-hashed — the
filename must be stable for scope + the browser's byte-comparison update check) and injects two `define`s from
the just-written `dist/manifest.json`:

- **`__SW_VERSION__`** = the short SHA-256 of the manifest JSON. Any asset-hash change ⇒ a new version ⇒ a new
  `sw.js` byte image ⇒ the browser detects an update. **A deploy that rebuilds the bundle automatically rotates
  the SW version** — no manual "bump `cinora-vN`" step.
- **`__PRECACHE__`** = the exact hashed asset URLs (`/dist/app-<hash>.css`, `/dist/site-<hash>.js`) plus
  `/offline` and the icons, precached on `install`.

`install` opens `${VERSION}-static` and `addAll(__PRECACHE__)`; `activate` deletes every cache whose name does
not start with `VERSION` (purging prior deploys) and `clients.claim()`s. `sw.js` + `manifest.webmanifest` are
served with **`Cache-Control: no-cache`** so an update is detected promptly, while the hashed assets keep
long-lived immutable caching.

### 3. Per-resource strategy + privacy-scoped offline

- **Hashed static assets + icons/fonts:** **cache-first** (immutable, versioned URLs).
- **TMDB posters (`https://image.tmdb.org`):** **cache-first, runtime, capped-LRU.** Caching the cross-origin
  response adds **no page-level CSP origin** (posters already load under `img-src https://image.tmdb.org`).
- **Navigations (`req.mode === "navigate"`):** **network-first → cache fallback → `/offline`.** Server-rendered
  Razor stays fresh; a previously-seen public page serves from cache offline; an uncached route → the designed
  `/offline` page.
- **Personalized navigations:** the app emits **`Cache-Control: no-store`** on authenticated responses; the SW
  **refuses to cache a `no-store` response** and, offline, serves `/offline` for them. This makes the offline
  scope **public reading only** (landing, `/discover`, Details, seen search pages) — never another user's inbox
  on a shared device. _(The mechanism that emits these headers — `PwaCacheControlMiddleware`, and the
  anonymous-HTML `no-cache` relaxation that makes public offline reading possible at all — plus the precise
  per-session scope, are recorded in the **Amendment 2026-07-06** below.)_
- **Any non-`GET`** (reviews, likes, `/push/*`, auth): **bypass the fetch handler entirely** — never cache
  writes, never touch anti-forgery. WebSocket (`/hubs/notifications`) passes through untouched.

### 4. Versioned update / rollout (consent-gated)

A new deploy installs the new SW as **waiting**. `site.ts` surfaces a **CSP-safe "New version available —
Refresh"** toast (`@alpinejs/csp`, reusing the Phase-3 toast grammar); on click it `postMessage("SKIP_WAITING")`;
the SW's `message` handler `skipWaiting()`s; a `controllerchange` listener reloads once. **Never an unconditional
`skipWaiting()`** — an in-use page (a half-typed review) is never hijacked mid-session.

### 5. Scope: built vs deferred

- **Built:** manifest + icons; the versioned SW (precache app shell + hashed assets; cache-first assets/posters;
  network-first navigations; `/offline`); the `no-store` privacy scope; the consent update flow. (`push`/
  `notificationclick` handlers land in the same worker in Milestone 6.2 — ADR 0020.)
- **Deferred (YAGNI):** **Background Sync** for offline write queuing (offline review drafts); periodic
  background poster refresh; Workbox. Recorded, not built.

## Consequences

**Positive**
- Deploy-safe caching: the SW cache is tied to the exact content-hashed bundle, so a deploy invalidates stale
  assets **automatically** (no manual version bump) — the PWA footgun is closed at the build.
- **Zero CSP change** — SW, manifest, and all SW fetches are same-origin/native; the strict policy is untouched
  (a reviewed property, §4 of the design).
- Privacy-correct offline: personalized pages are never cached; only public reading works offline, with a
  designed fallback.
- Integrates with the existing esbuild/`manifest.json`/`IAssetManifest` pipeline — one new esbuild entry, no new
  build tool, no CDN.

**Negative / accepted**
- Offline shows `/offline` (not the real page) for authenticated routes — a deliberate privacy trade-off.
- No offline writes in Phase 6 (Background Sync deferred).
- A hand-authored SW (vs. Workbox) is more code to own, but it is small, auditable, dependency-free, and
  CSP-clean — consistent with the project's "avoid a needless dependency" posture (ADR 0005).
- `sw.js`/`manifest.webmanifest` need `Cache-Control: no-cache` (a header tweak, not a CSP/security change).

## Amendment (2026-07-06) — `PwaCacheControlMiddleware`: the anti-forgery cache relaxation and the exact offline read scope

_Recorded at the Milestone 6.1 architecture + security exit gates, which confirmed the shipped
`PwaCacheControlMiddleware` (Web) **correct and privacy-safe** but asked that its cache policy — more nuanced than
§3 (and the design's §2.3) describe — be captured in this ADR. Doc-only reconciliation, **no behavior change**;
the §3 text above is retained as the design intent and this amendment sharpens it._

**1. The anti-forgery ↔ cache interaction (why anonymous public HTML lands on `no-cache`, not fresh-and-uncacheable).**
`_Layout.cshtml` renders the HTMX anti-forgery token in the `<head>` on **every** page
(`@Antiforgery.GetAndStoreTokens(Context)`), so ASP.NET Core anti-forgery stamps **`Cache-Control: no-cache,
no-store`** (`SetDoNotCacheHeaders`) on **every** HTML response — anonymous pages included. Left untouched, that
makes the SW's pages cache **impossible to fill**: a `no-store` response is one the SW refuses to store, so
offline browsing (the 6.1 exit criterion) could never work. `PwaCacheControlMiddleware` — decided from a
`Response.OnStarting` callback (so it runs after view rendering and **wins over** the framework header) and
registered **after** `UseAuthentication`/`UseAuthorization` (so both `HttpContext.User` and the response
`Content-Type` are known) — therefore shapes only `text/html` responses as:

- **Anonymous public GET HTML → `no-cache`** — the framework's blanket `no-store` is **deliberately relaxed** to
  cacheable-with-revalidation (**never `no-store`**), so the SW may cache the landing / `/discover` / Details /
  search pages for offline reading, while shared and HTTP caches still **revalidate** (no page is served stale
  without a round-trip).
- **Authenticated HTML → `no-store`** (the unchanged §3 privacy rule) — the SW never stores one user's
  personalized pages, so a second user on a shared device cannot read them offline.
- **`/offline` → `no-cache`** always (even for an authenticated viewer) — the SW precaches it; marking it
  `no-store` would remove the offline fallback.
- **`/account/*` auth-form pages → kept `no-store`** — **excluded** from the anonymous relaxation. They are
  anti-forgery-token-bearing, sit outside the offline read scope, and gain nothing from being cacheable, so they
  retain the framework's `no-store`.
- **Anonymous non-`GET`** (e.g. a failed-login re-render) keeps the framework's `no-store` intact; the SW ignores
  non-`GET` anyway.

**CSRF is unaffected by the relaxation.** Anti-forgery is enforced by the double-submit **token↔cookie check on
POST**, not through response caching; relaxing a GET page from `no-store` to `no-cache` changes no anti-forgery
guarantee, and `no-cache` still forces a revalidation round-trip so no response is served without the server
confirming it. Both the architecture and security review gates confirmed this property.

**2. The exact offline read scope (sharpening §3 / design §2.3).** Because **all** authenticated HTML is
`no-store`, the SW's offline read scope is **exactly the anonymous public pages already visited** (landing,
`/discover`, Details, seen search) — served from the pages cache. Consequently **an authenticated session gets
`/offline` for *every* route offline** — including otherwise-public pages — because its responses were never
cacheable. That is **stricter** than §3's "a previously-seen public page serves from cache offline" (and the
design's §2.3 "already-visited public page served from cache") wording implies: a *signed-in* user offline sees
`/offline` everywhere, not the cached public page. This is the **deliberate privacy decision** — one user's
session must not leave cached pages a second user could read on a shared device — not a regression. §3's wording
is reconciled by this clarification, not rewritten.

## Alternatives considered

1. **Cache-everything transparent SW.** Rejected — stale assets after deploy, and it would cache personalized
   pages (privacy). The per-resource strategy + `no-store` scope is the fix.
2. **A fixed `cinora-vN` version bumped by hand per deploy (the skill's literal example).** Rejected as the
   mechanism — error-prone (a forgotten bump ships stale assets). Keying `VERSION` to the manifest hash
   automates it; the skill's intent (bump per deploy) is satisfied by the build.
3. **Workbox.** Rejected — MIT/free but a heavier dependency with its own build + precache-manifest tooling; a
   ~40-line hand-authored fetch handler is smaller, auditable, and CSP-clean. Revisit only if the caching
   strategy grows complex.
4. **Serve `sw.js` content-hashed like other assets.** Rejected — the SW filename must be **stable** at the root
   for scope and for the browser's byte-comparison update detection; its content changes (via `__SW_VERSION__`)
   are what trigger updates.
5. **Cache authenticated pages with per-user cache partitioning.** Rejected — the SW can't read HttpOnly cookies
   to partition safely; `no-store` + refuse-to-cache is simpler and provably private.

## Related
- ADR 0004 (free/local-only — no Azure, no Docker, no CDN), ADR 0011 (same worker gains push in 6.2), ADR 0013
  (same-origin serving / no-CSP-widening precedent), ADR 0021 (deployment — publish output must include
  `sw.js`/manifest/icons; `no-cache` headers).
- `docs/architecture/phase-6-polish-deployment-design.md` §2 (PWA), §4 (CSP contract).
- Skills: `.claude/skills/pwa-service-worker/SKILL.md`, `.claude/skills/typescript-frontend/SKILL.md`,
  `.claude/skills/responsive-accessibility/SKILL.md`.

---

_Design-only ADR authored 2026-07-03 (verified: `build.mjs` esbuild + `manifest.json` + `IAssetManifest`;
`SecurityHeadersMiddleware` strict CSP `default-src 'self'`; `_Layout.cshtml` head links `@Assets.Resolve`; no
`sw.js`/`manifest.webmanifest`/`/offline` exist in `src/`). No application code written._

_Amendment (2026-07-06) verified against shipped Milestone-6.1 code: `src/Cinora.Web/Infrastructure/PwaCacheControlMiddleware.cs`
(`Response.OnStarting` callback; shapes `text/html` only; `/offline` → `no-cache`; authenticated → `no-store`;
anonymous GET → `no-cache`; anonymous non-`GET` left as the framework's `no-store`), registered via
`UsePwaCacheControl()` **after** `UseAuthentication`/`UseAuthorization` in `src/Cinora.Web/Program.cs`;
`src/Cinora.Web/Views/Shared/_Layout.cshtml` renders `@Antiforgery.GetAndStoreTokens(Context)` in the head on
every page; the `/offline` fallback exists (`src/Cinora.Web/Views/Pwa/Offline.cshtml`). **`/account/*` →
`no-store` exclusion — shipped:** the exclusion (Amendment §1) **is present** in `PwaCacheControlMiddleware`
(confirmed at the Phase-6 security + code exit gates, 2026-07-06) — auth-form GETs are kept `no-store`, not
relaxed to `no-cache`, so the SW never caches a login/register page. The whole Amendment §1 rule set is now
realized in code. No behavior in the accepted Decision changed._

Last verified against code: 2026-07-06
