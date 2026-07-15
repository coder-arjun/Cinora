# Cinora — Phase 6 Polish & Deployment Solution Design (Authoritative)

- **Status:** Accepted for Phase 6 (Polish & Deployment) — pre-implementation design review
- **Date:** 2026-07-03
- **Owner:** architecture-agent
- **Builds on:** [Phase 1 solution structure](solution-structure.md), [Phase 2 Discovery design](phase-2-discovery-design.md),
  [Phase 3 Reviews & Social design](phase-3-social-design.md), [Phase 4 Watchlists & Profile design](phase-4-watchlists-profile-design.md)
- **New ADRs:** [0019 PWA service-worker caching & versioned update](../adr/0019-pwa-service-worker-caching-and-update.md),
  [0020 Web Push (self-VAPID) over Device + notification-preferences model](../adr/0020-web-push-and-notification-preferences.md),
  [0021 Free/self-hosted deployment posture (plans-only) & performance-polish stance](../adr/0021-deployment-posture-and-performance-polish.md)
- **Governing ADRs:** [0001 Layering](../adr/0001-clean-architecture-layering.md),
  [0004 Free/local-only](../adr/0004-free-local-only-infrastructure.md),
  [0005 Hand-rolled mediator](../adr/0005-hand-rolled-mediator.md),
  [0009 Resource ownership & the current-user seam](../adr/0009-resource-ownership-and-current-user-seam.md),
  [0011 Self-hosted SignalR realtime](../adr/0011-self-hosted-signalr-realtime.md),
  [0013 `IFileStorage` & avatar serving](../adr/0013-file-storage-and-avatar-serving.md),
  [0015 Notification preferences deferred to Phase 6](../adr/0015-notification-preferences-deferred.md) — **un-deferred here**

This document is the single source of truth for Phase 6 (Polish & Deployment). It designs the **installable
PWA** (manifest + service worker + offline), **Web Push** (self-generated VAPID over the existing `Device`
entity), the **notification-preferences** model (un-deferring ADR 0015), the **performance polish** pass
(closing the accumulated `REVIEW_BACKLOG.md` perf/polish items), and the **plans-only, free/local/self-hosted
deployment** posture, then gives a delegable milestone build order. Implementation agents follow it;
deviations require an ADR.

> **Design-only.** No code was written and no build/test was run producing this document. Every `Verify`
> command in §12 is an acceptance check the *implementing* agent must run and show output for.

> **Governing constraints (unchanged; restated because Phase 6 is the ship gate):**
> - **Free / local-only / no Docker / no subscriptions (ADR 0004).** The phase brief names **Azure App
>   Service + Azure SQL + Azure Cache for Redis + Azure Blob + Azure SignalR** and **paid push**. All are
>   replaced by the free, no-Docker, on-machine stack already shipped: **SQL Server Express/LocalDB**,
>   **in-memory `IDistributedCache`**, **local-filesystem `IFileStorage`**, **self-hosted SignalR**, and
>   **Web Push with self-generated VAPID keys** (free — no third-party push gateway). **Deployment is
>   PLANS-ONLY:** the runbook and scripts are authored for the human to run locally; **the orchestrator and
>   every agent NEVER deploy, never `git`, never provision cloud infrastructure.**
> - **The devops-agent is NOT used on this project (user preference).** Its purely-local Phase-6 duties —
>   the local build/publish/run scripts, environment configuration, Options wiring, `/health`, and the
>   deployment runbook — are owned by **backend-agent** (+ documentation-agent for the written runbook).
> - **Hand-rolled mediator (ADR 0005).** Every "`ISender`/mediator" reference is the in-house type in
>   `Cinora.Application/Common/Messaging`. **Never add MediatR. Never add FluentAssertions** (tests use
>   xUnit `Assert` + NSubstitute). Free OSS libraries (Serilog, esbuild, Alpine, HTMX, `@microsoft/signalr`)
>   are permitted; the one **new** server dependency this phase — a Web Push library — must be a
>   permissively-licensed (MIT) free package, license-verified at add-time (§3.2, ADR 0020).
> - **No CSP widening (§4).** The service worker, the web app manifest, the Push API subscribe POST, and
>   push delivery are **all same-origin or browser-native**; VAPID uses *our own* keys (no third-party push
>   origin the page connects to). Phase 6 ships the entire PWA + push surface **without touching
>   `SecurityHeadersMiddleware`** — a reviewed, justified property, not an accident.
> - **Fail-closed contracts (Phase 1/3).** Global fallback authZ (`[Authorize]` unless `[AllowAnonymous]`);
>   global `AutoValidateAntiforgeryToken` (HTMX/`fetch` writes carry the token via the
>   `RequestVerificationToken` header); per-IP/per-user rate-limit policies. Phase 6 honors these on every
>   new endpoint (`/push/*`, `/settings/notifications`).
> - **No generic repository; no business rules in handlers; ownership enforced in the handler → 403 (ADR
>   0009); no external DTO into Domain; no `IConfiguration` injected (bind Options).** All reaffirmed.

---

## 1. What Phase 6 adds and where every concern lives

Phase 6 adds **no new business feature** — it makes the existing app *installable, reachable out-of-app,
fast, accessible, and deployable*. It builds on **existing Phase-1 entities** — `Device`
(`Register(userId, endpoint, p256dh, auth)`, `Seen()`; the Web-Push transport, already shaped for this) and
`Notification` (`Type`, `TargetId`, `Message`, `IsRead`) — and reuses the **ADR-0011 post-commit best-effort
dispatch seam** rather than duplicating it. The only schema delta is **additive** (notification-preference
columns on `Users`, an `EndpointHash` dedup column + index on `Device`; §9). The layering and dependency rule
are unchanged and inviolable (ADR 0001).

| Concern | Layer / location | Notes |
|---|---|---|
| **Web app manifest** (`manifest.webmanifest`: `name`, `short_name`, 192/512 + maskable icons, `display:"standalone"`, dark `theme_color`/`background_color`, `start_url`, `scope`) | `Cinora.Web/wwwroot/manifest.webmanifest` + icons under `wwwroot/icons/` | NEW. Static, same-origin. Linked from `_Layout.cshtml` head. ADR 0019 §1. |
| **Service worker** (versioned cache proxy: app-shell precache, cache-first hashed assets, network-first navigations, `/offline` fallback, `push`/`notificationclick`, `SKIP_WAITING`) | `Cinora.Web/Scripts/sw.ts` → esbuild → `wwwroot/sw.js` (**root scope, stable name, NOT hashed**) | NEW. TS-authored, bundled by the existing `build.mjs` as a **second entry**; its `VERSION` + precache list are injected from the content-hashed `manifest.json` so a deploy invalidates stale caches. ADR 0019. |
| **`/offline` fallback page** + `Cache-Control: no-store` on authenticated responses | `Cinora.Web/Controllers/PwaController.cs` (`[AllowAnonymous] GET /offline`) + a small response-header middleware/filter | NEW. `/offline` is public + cacheable; personalized pages emit `no-store` so the SW never caches another user's inbox/feed on a shared device (privacy-correct offline scope). ADR 0019 §3. |
| **`IPushSender` port** (`SendAsync(PushSubscription sub, PushPayload payload, ct) → PushSendResult`) | `Cinora.Application/Common/Interfaces/IPushSender.cs` | NEW. Speaks Application-owned primitives (endpoint + keys + JSON payload); **never** the WebPush SDK type or a `Device` entity on the wire. `PushSendResult` distinguishes `Delivered` / `Gone` (404/410) / `TransientFailure`. ADR 0020 §2. |
| **`WebPushSender` adapter** (self-VAPID; free MIT WebPush library) | `Cinora.Infrastructure/Push/WebPushSender.cs` | NEW. Binds `WebPushOptions` (VAPID subject + public/private key). The **only** new server dependency; MIT, license-verified. A future paid gateway is a drop-in behind the port. ADR 0020 §3. |
| **`IPushDispatch` port** (`Enqueue(Guid notificationId)`) + its two free adapters | port: `Cinora.Application/Common/Interfaces/IPushDispatch.cs`; adapters: `Cinora.Infrastructure/Push/{InlinePushDispatch,HangfirePushDispatch}.cs` | NEW. Keeps push **off the request's critical path** (§3.4). Ships the **`InlinePushDispatch`** (own DI scope + fire-and-forget best-effort) now; a **`HangfirePushDispatch`** (durable/retried) is the drop-in when Hangfire lands (Phase 5). Port stays either way (ADR 0004 posture). |
| **`SendPushNotificationCommand` + handler** (load devices → check preference → build payload+deep-link → send per device → prune `Gone`) | `Cinora.Application/Features/Notifications/SendPushNotificationCommand.cs` | NEW. The push orchestration, keyed by `notificationId`. Reads `IAppDbContext.Devices`/`Users`/`Notifications`; calls `IPushSender`; deletes 404/410 devices. Runs inside the `IPushDispatch` job/scope, not the producing handler. ADR 0020 §3.4. |
| **Push subscribe/unsubscribe endpoints** (`PushController` `[Authorize]`) | `Cinora.Web/Controllers/PushController.cs` (`POST /push/subscribe`, `POST /push/unsubscribe`, `GET /push/public-key`) | NEW. `subscribe` upserts a `Device` for the current user (server-resolved, ADR 0009); anti-forgery + a per-user `social-write` rate limit. Same-origin `fetch` → `connect-src 'self'` (no CSP change). ADR 0020 §3.1. |
| **`NotificationPreferences` owned value object** on `User` (per-type push opt-out, default-on) + `User.UpdateNotificationPreferences(...)` | `Cinora.Domain/Entities/User.cs` + `Cinora.Domain/ValueObjects/NotificationPreferences.cs` | NEW. Owned type ⇒ **columns on the `Users` table, no join, no new entity** (un-defers ADR 0015 with its recorded future shape). Consulted by the push dispatcher before send; **in-app row always persists** (§5). ADR 0020 §4. |
| **Notification-settings vertical + surface** — `GetNotificationSettingsQuery`, `UpdateNotificationPreferencesCommand`; extends `SettingsController` | `Cinora.Application/Features/Profiles/` (extend) + `Cinora.Web/Controllers/SettingsController.cs` (`GET/POST /settings/notifications`) | NEW. Owner-only (`ICurrentUser`, no id bound); replaces the Phase-4 "arriving with push" placeholder (§5.3). |
| **`NotificationDeepLink.Resolve(...)`** — the single source of the notification→URL route grammar | `Cinora.Application/Features/Notifications/NotificationDeepLink.cs` | NEW. **DRY consolidation:** the route grammar currently duplicated inline in `_NotificationItem.cshtml` (the `type switch → "/discover/title/…"`, `"/friends"`, `"/users/{actor}"`) is single-sourced here so the push payload's `data.url` and the inbox card agree. Reuses the enriched coords from `NotificationProjection`. |
| **Performance polish** — response/static compression, avatar-serving ETag, feed-body SQL truncation, bundle audit + SignalR lazy-load, (measure-gated) OutputCache, `CacheLog` query redaction | `Cinora.Web/Program.cs`, `Cinora.Web/Controllers/MediaController.cs` + `Infrastructure/Storage/LocalFileStorage.cs`, `Application/Features/.../FeedItemProjection`, `Scripts/site.ts`, `Infrastructure/Tmdb/CacheLog.cs` | See §6 + the REVIEW_BACKLOG closure table (§6.7). Owned by performance-agent + backend-agent. |
| **`WebPushOptions`** (VAPID subject + public/private key; `PushOptions` TTL/urgency defaults) | `Cinora.Infrastructure/Options/WebPushOptions.cs` (`SectionName`, `ValidateUsingDataAnnotations`, `ValidateOnStart` ON in 6.2) | NEW. Bound in `AddInfrastructure`; **private key from user-secrets/env, never appsettings**. Bind Options — never inject `IConfiguration` (§10). |
| **Deployment (plans-only)** — runbook, `artifacts/migrate.sql` regen, DP key-ring persistence, publish verification, env config, `ForwardedHeaders`-if-proxied | `docs/deployment/runbook.md` (documentation-agent) + `scripts/` (existing `db-update.ps1`/`dev.ps1`) | §8, ADR 0021. **No new hosting; no Docker; the human runs it.** |

**Anti-patterns still banned (reaffirmed):** no generic `IRepository<T>`; no business rules in handlers (the
preference invariant lives on `User`/`NotificationPreferences`, not the dispatcher); no `IConfiguration` in
services; **no CSP widening** (§4); **no `Database.Migrate()` on startup** (§8); **no secret in an
`appsettings.*.json`** (the VAPID private key + `FileStorage:UrlSigningKey` + Google creds are
user-secrets/env). No `Html.Raw` on user content; push payload text is treated as **plain text** by the
service worker and set via `showNotification({ body })` (never `innerHTML`).

---

## 2. PWA — manifest + service worker (ADR 0019)

### 2.1 Web app manifest (installability)

`wwwroot/manifest.webmanifest` (served with `Content-Type: application/manifest+json`), linked once in the
`_Layout.cshtml` head: `<link rel="manifest" href="/manifest.webmanifest">`. Installability criteria the
manifest + SW must jointly satisfy (Chromium/Safari): served over HTTPS (Phase-1 `UseHttpsRedirection`
already enforces it), a registered service worker with a `fetch` handler, `name` + `short_name`, a
`start_url`, `display: "standalone"`, and icons including **192×192 and 512×512 PNG** plus a **`maskable`**
variant. Fields:

| Field | Value | Why |
|---|---|---|
| `name` / `short_name` | `"Cinora"` / `"Cinora"` | Install prompt + home-screen label. |
| `start_url` | `"/"` (landing → redirects by auth state) | Launch target; same-origin. |
| `scope` | `"/"` | SW controls the whole origin. |
| `display` | `"standalone"` | App-like, no browser chrome. |
| `theme_color` / `background_color` | the dark-luxury surface hex from `Styles/app.css @theme` `--color-surface-0` (frontend-agent picks the exact token value) | Matches the premium dark UI; `background_color` paints the splash before first paint (no white flash). |
| `icons` | `192`, `512` (`purpose:"any"`) + `512` (`purpose:"maskable"`), PNG under `wwwroot/icons/` | Installability + Android adaptive icons; same-origin (`img-src 'self'`). |
| `orientation` | `"portrait"` (optional) / omit | Product/UX call. |

Icons are static same-origin assets — **no CSP impact** (`img-src 'self'` already covers them; the manifest
fetch itself is governed by `manifest-src`, which falls back to `default-src 'self'` — §4).

### 2.2 Service-worker lifecycle + caching strategy (integrated with the esbuild pipeline)

The service worker is **authored in TypeScript** (`Scripts/sw.ts`) and bundled by the existing `build.mjs` as
a **second esbuild entry**, output to **`wwwroot/sw.js`** — at the web root so its scope is `/` (the skill's
"serve `sw.js` from site root" rule), and with a **stable, non-content-hashed name** so the browser's
byte-comparison update check can find it. Registration lives in `Scripts/site.ts` (already bundled):
`if ("serviceWorker" in navigator) navigator.serviceWorker.register("/sw.js")` — a progressive-enhancement
guard so unsupported browsers simply run online-only.

**Keying the cache to the content-hashed manifest (the "stale assets after deploy" fix).** `build.mjs` already
emits `wwwroot/dist/manifest.json` mapping `app.css`/`site.js` → their content-hashed URLs. Phase 6 extends
`build.mjs` to, when building `sw.ts`, inject two esbuild `define`s from that manifest: a **`__SW_VERSION__`**
(the short SHA-256 of the manifest JSON, so any asset-hash change ⇒ a new SW version ⇒ a new `sw.js` byte
image ⇒ the browser detects an update) and a **`__PRECACHE__`** (the exact hashed asset URLs to precache).
This means a deploy that rebuilds the bundle **automatically** rotates the SW version and purges stale caches
— no manual version bump, closing the skill's "bump per deploy" step into the build.

```
install   → caches.open(`${VERSION}-static`).addAll([ "/offline", ...__PRECACHE__ ])   // app shell + hashed assets
activate   → delete every cache whose name does not start with VERSION; clients.claim()  // purge old deploys
message   → if (data === "SKIP_WAITING") self.skipWaiting()                              // update on consent only
fetch      → strategy per request (below)
push / notificationclick → §3.5
```

**Per-resource strategy (deliberate, never cache-everything):**

| Request | Strategy | Rationale |
|---|---|---|
| `GET` hashed static assets (`/dist/app-*.css`, `/dist/site-*.js`, `/icons/*`, fonts) | **Cache-first** | Content-hashed URLs are immutable; a byte change is a new URL, so cache-first is always fresh. |
| `GET` TMDB posters (`https://image.tmdb.org/...`) | **Cache-first, runtime, capped LRU** | Immutable poster URLs; opaque cross-origin responses cache fine and cut repeat bytes. **No CSP impact** — the SW caching a cross-origin `img` response does not add a page `connect-src`/`img-src` origin (posters already load under `img-src https://image.tmdb.org`, §4). Bound the poster cache (e.g. ~60 entries, evict oldest). |
| `GET` navigations (`req.mode === "navigate"`) | **Network-first → cache fallback → `/offline`** | Server-rendered Razor stays fresh online; the cache serves a previously-seen page offline; an uncached route falls back to the designed `/offline`. |
| **Personalized navigations** (response carries `Cache-Control: no-store`) | **Network-first, DO NOT store** | Privacy: the SW must never cache one user's `/home` feed, `/notifications`, `/settings`, `/friends`, `/watchlist` where a shared-device second user could read it offline. The app emits `no-store` on authenticated responses (§2.3); the SW refuses to `cache.put` a `no-store` response and, offline, serves `/offline` for these. |
| Any non-`GET` (POST/PUT/PATCH/DELETE — reviews, likes, `/push/*`, auth) | **Bypass the fetch handler entirely** | Never cache writes; never interfere with anti-forgery. `if (req.method !== "GET") return;` |
| `wss://` SignalR (`/hubs/notifications`) | **Not intercepted** | WebSocket upgrade is outside the `fetch` cache model; passes through. |

### 2.3 Offline support scope (which reading flows work offline)

- **Works offline (public, cacheable):** the app shell (CSS/JS/icons), any **already-visited** public page —
  the **landing**, **`/discover`**, a **title `/discover/title/{media}/{tmdbId}` Details** page, **search
  result** pages you have seen — served from the PAGES cache; posters from the poster cache. An
  **uncached** public route offline → the designed **`/offline`** page (a branded "you're offline" with a
  retry, matching the dark theme + the established empty/error-state grammar).
- **Deliberately NOT offline (personalized):** `/home` (friends feed), `/notifications`, `/settings`,
  `/friends`, `/watchlist`, and every write. These emit `Cache-Control: no-store`, so offline they show
  `/offline` rather than a stale or another-user's cached view. This is a **privacy decision**, not a gap —
  recorded in ADR 0019.
- **Writes offline:** not queued in Phase 6 (no Background Sync). A write attempted offline surfaces the
  existing `htmx:responseError`/network-error affordance. Background Sync for offline review drafts is a
  documented future enhancement (ADR 0019 §alternatives), not shipped (YAGNI).

### 2.4 Versioned update / rollout (clients pick up new versions)

The house pattern (skill): a new deploy ⇒ new `__SW_VERSION__` ⇒ the browser installs the new `sw.js` as the
**waiting** worker. `site.ts` listens for `registration.onupdatefound` / a waiting worker and shows a
**CSP-safe "New version available — Refresh"** affordance (an unobtrusive toast reusing the Phase-3 toast
grammar, name-only `@alpinejs/csp`). On click it `postMessage("SKIP_WAITING")` to the waiting worker; the SW's
`message` handler calls `skipWaiting()`; a `controllerchange` listener in `site.ts` then reloads once. **Never
an unconditional `skipWaiting()`** — an in-use page (a half-typed review) is never hijacked mid-session.
**Caching correctness for the SW file itself:** `sw.js` and `manifest.webmanifest` are served with
`Cache-Control: no-cache` (revalidate every load) so an update is detected promptly; the *hashed assets* they
point at keep long-lived immutable caching. A tiny static-file `OnPrepareResponse` (or a mapped endpoint) sets
that header for those two paths — a header tweak, **not** a CSP change.

---

## 3. Web Push — self-VAPID over the `Device` entity (ADR 0020)

Push is **out-of-app reach** for the same `Notification` events SignalR already delivers **in-app**
(ADR 0011): SignalR is the live accelerator when a tab is open; Web Push reaches a user whose tab is closed.
Both hang off the **same post-commit best-effort seam** — the inbox row is always the source of truth.

### 3.1 Subscribe flow + permission UX (opt-in, contextual)

- **Permission is requested only after a deliberate user gesture** (an "Enable notifications" control in
  `/settings/notifications`, or a contextual "Notify me" affordance) — **never** on landing or page load
  (a page-load prompt earns a permanent origin-level `denied`). Progressive enhancement: the control is
  hidden/disabled when `!("serviceWorker" in navigator && "PushManager" in window)`.
- On click: `Notification.requestPermission()` → if `granted`, `registration.pushManager.subscribe({
  userVisibleOnly: true, applicationServerKey: <VAPID public key, base64url→Uint8Array> })`. The VAPID
  **public** key is fetched from **`GET /push/public-key`** (or embedded as a `<meta>` — non-secret).
- The browser `PushSubscription` (`endpoint`, `keys.p256dh`, `keys.auth`) is `POST`ed to **`/push/subscribe`**
  (same-origin `fetch` with the `RequestVerificationToken` header — anti-forgery honored). The `PushController`
  (`[Authorize]`, `social-write` rate limit) unwraps it into a `RegisterDeviceCommand(endpoint, p256dh, auth)`
  and **upserts** a `Device` for the **server-resolved** current user (ADR 0009 — no user id from the client).
- **Degrade gracefully** when unsupported or denied: the app still delivers **in-app** notifications
  (inbox + SignalR toast). Push is strictly additive; nothing breaks without it.

### 3.2 The push library (free, MIT) behind a port

`WebPushSender : IPushSender` (Infrastructure) wraps a **free, permissively-licensed (MIT) Web Push .NET
library** (e.g. the `WebPush` package by web-push-libs, or `Lib.Net.Http.WebPush`) that implements **RFC 8291**
(payload encryption, `aes128gcm`) and **RFC 8292** (VAPID `Authorization`). The license is **verified at
add-time** and the package **pinned centrally** in `Directory.Packages.props` (CPM) — an MIT OSS library is
permitted (unlike the commercially-licensed MediatR/FluentAssertions/ImageSharp the project banned). VAPID
key material comes from `WebPushOptions` (bound, not `IConfiguration`). The alternative — **hand-rolling
RFC 8291/8292 over the BCL** (`ECDiffieHellman`, `HKDF`, `AesGcm`, `ECDsa` all exist in .NET 10) — is the
zero-dependency fallback if the library license ever fails re-verification, and is recorded in ADR 0020; it is
**not** the primary because the encryption is fiddly and a vetted library is lower-risk.

### 3.3 Storing subscriptions on `Device` (upsert + dedup)

The existing `Device` entity already carries exactly the transport fields (`Endpoint`, `P256dhKey`,
`AuthSecret`, `CreatedAtUtc`, `LastSeenUtc`, `Register(...)`, `Seen()`). Phase 6 adds **one dedup mechanism**:
a persisted **`EndpointHash`** (SHA-256 of the endpoint, computed in `Device.Register` via the BCL — no
external dep, no dependency-rule breach) + a **unique index `(UserId, EndpointHash)`** (the raw
`nvarchar(2048)` endpoint cannot be indexed — it exceeds SQL Server's 900-byte key limit, per the existing
`DeviceConfiguration` comment — so the fixed-width hash is the index key). `RegisterDeviceCommand` then
**upserts**: match the current user's Device by `(UserId, EndpointHash)`; if found, refresh keys + `Seen()`
(a re-subscribe rotates keys); else insert. **One subscription per Device, many Devices per user** (phone +
desktop). The no-schema-change alternative — load the user's Devices by the existing `UserId` index and match
the endpoint in memory (device counts are tiny) — is acceptable and noted, but the hash+index is race-safe and
preferred since Phase 6 ships a migration anyway (§9).

### 3.4 Sending pushes — reuse the ADR-0011 dispatch seam, off the critical path

The producing handlers (like/comment/friend-request/friend-accept) already: co-persist the `Notification`
row in their own `SaveChangesAsync`, then call the shared **`RealtimeNotificationDispatcher`** for the
best-effort SignalR push. Phase 6 **extends that one seam** — it does **not** add push logic to four handlers
independently — by having the dispatcher, after the SignalR push, call **`IPushDispatch.Enqueue(notification.Id)`**.
The four producing handlers inject `IPushDispatch` and pass it through (a mechanical, DRY-preserving change;
the alternative — a second explicit call in each handler — is noted in ADR 0020). Enqueue happens **after
commit** (the id exists) and is **fire-and-forget best-effort**: a push failure never rolls back or fails the
write (the inbox is authoritative), exactly the N2/ADR-0011 contract.

`IPushDispatch` has two free adapters (ADR-0004 "port stays, free provider ships"):
- **`InlinePushDispatch` (ships now):** spins its **own DI scope** (`IServiceScopeFactory`) on a background
  `Task` so the send runs **off the request thread** and **never touches the request's disposed scope**;
  resolves a fresh `ISender` and dispatches `SendPushNotificationCommand(notificationId)`; bounded timeout;
  logs a Warning on failure. This keeps slow/flaky push endpoints off request latency without Hangfire.
- **`HangfirePushDispatch` (drop-in when Hangfire lands, Phase 5):** `BackgroundJob.Enqueue(...)` the same
  command — durable, retried, dashboard-visible. Swapping the DI registration is the only change.

`SendPushNotificationCommandHandler` (Application): load the `Notification` (type, recipient, target); **check
the recipient's per-type push preference (§5) and return early if opted out — the in-app row still stands**;
otherwise load the recipient's `Device`s, build the `PushPayload` (title/body from the notification message +
`data.url` from `NotificationDeepLink.Resolve(...)` reusing the `NotificationProjection` enriched coords), and
`IPushSender.SendAsync` per device. On a `PushSendResult.Gone` (**404/410**) **delete that `Device`** (the
subscription is dead); on `TransientFailure` log and move on (retried only under the Hangfire adapter).

### 3.5 Notification-click deep links + the service-worker push handlers

In `sw.ts` (same worker as §2):
- **`push`** — `event.data?.json()` → `self.registration.showNotification(title, { body, icon, badge, data: { url }, tag })`. Text is set via the `body`/`title` options (**plain text**, never `innerHTML`); `tag` coalesces repeats. `userVisibleOnly` guarantees every push shows UI (Chrome requirement).
- **`notificationclick`** — `event.notification.close()`, then `clients.matchAll({ type: "window" })`: **focus an existing Cinora window** and navigate it to `data.url` if one is open; otherwise `clients.openWindow(data.url)`. Never blindly opens a new tab. `data.url` is the **same-origin relative deep link** from `NotificationDeepLink` (a Details `#review-{id}` anchor, `/friends`, or `/users/{actor}`), single-sourced with the inbox card (§1 DRY item).

### 3.6 Expired-subscription cleanup

- **Primary (always runs, free):** the **404/410 → delete** on send (§3.4) prunes dead endpoints exactly when
  they prove dead — the aggressive, self-healing path the skill mandates.
- **Optional sweep (when Hangfire lands):** a recurring job deletes `Device` rows whose `LastSeenUtc` is older
  than a TTL (e.g. 90 days) — belt-and-suspenders. Not required in Phase 6 (the on-send prune suffices); noted
  in ADR 0020.

---

## 4. Anti-forgery / authZ / CSP contract — **no CSP widening** (load-bearing)

Every Phase-6 endpoint honors the standing contracts (Phase 1/3): **fail-closed authZ** (`PushController`,
`SettingsController` notifications actions are `[Authorize]`; `/offline` and `/manifest.webmanifest`/`/sw.js`
static are anonymously reachable — the SW/manifest must load pre-auth, so the static-file/`/offline` paths are
`[AllowAnonymous]`/static), **global `AutoValidateAntiforgeryToken`** (the `/push/subscribe` `fetch` sends the
`RequestVerificationToken` header — no opt-out, no new wiring), and **rate limits** (`social-write` per-user on
the push writes; `/push/public-key` is a trivial anonymous constant — a light `PublicRead`-style limit or
none).

**CSP: unchanged (`SecurityHeadersMiddleware` is not touched).** Directive-by-directive justification:

| Phase-6 capability | Governing CSP directive | Why the current strict policy already admits it |
|---|---|---|
| `<link rel="manifest">` fetch | `manifest-src` → falls back to `default-src 'self'` | Same-origin manifest; `default-src 'self'` covers it. |
| Service-worker script (`/sw.js`) | `worker-src` → falls back to `child-src` → `script-src` → `default-src 'self'` | Same-origin worker; `default-src`/`script-src 'self'` covers it. |
| SW-initiated same-origin fetches (navigations, `/dist/*`, `/offline`) | `connect-src 'self'` / `default-src 'self'` | All same-origin. |
| SW caching TMDB posters | `img-src 'self' https://image.tmdb.org data:` (already applied, Phase 2) | Posters already load from that origin; the SW caching the response adds **no page-level origin**. |
| `POST /push/subscribe`, `GET /push/public-key` (same-origin `fetch`) | `connect-src 'self'` | Same-origin. |
| `PushManager.subscribe` + push delivery + `showNotification` | **browser-native Push API** — not a page network request | Not governed by page CSP; VAPID uses **our own** keys, so there is **no third-party push origin the page connects to**. |
| PWA icons | `img-src 'self'` | Same-origin. |

There is **no** third-party CDN, no external font, no remote push gateway origin, no inline/eval script
(`sw.ts`/`site.ts` are bundled; the update toast uses `@alpinejs/csp`). **If a reviewer expects a CSP change,
the answer is deliberate: none is needed, and adding one would be a regression.** The only new response
headers are `Cache-Control: no-cache` on `/sw.js` + `/manifest.webmanifest` and `Cache-Control: no-store` on
authenticated pages — cache directives, not security-policy widening.

---

## 5. Notification preferences — un-deferring ADR 0015 (ADR 0020 §4)

ADR 0015 deferred preferences to Phase 6 "where Web Push actually lands and a delivery channel exists for a
preference to govern," with the recorded future shape. Phase 6 realizes that shape.

### 5.1 The model — an owned value object on `User` (columns, no new entity, no join)

```
Cinora.Domain/ValueObjects/NotificationPreferences.cs   (owned type)
  bool PushFriendRequests   = true
  bool PushFriendAccepted   = true
  bool PushReviewLikes      = true
  bool PushComments         = true
  bool IsPushEnabled(NotificationType type) => type switch { … }
  NotificationPreferences With(NotificationType, bool) / All-args factory
```

`User` gains `NotificationPreferences Preferences { get; private set; }` (default = all-`true`) and a domain
method `UpdateNotificationPreferences(NotificationPreferences preferences)`. Mapped as an **EF owned type**
⇒ four **`bit NOT NULL DEFAULT 1`** columns on the **`Users`** table — **no new table, no join, additive
migration** (§9). This is the ADR-0015 "small set of per-event opt-in flags owned by the user" option, chosen
over a dedicated `NotificationPreference` entity because (a) there is exactly **one channel now** (push;
in-app is always-on) and **four** event types — a join per push would be waste, and (b) YAGNI applies to
architecture. The **upgrade path** (email or in-app-mute arriving later → a per-`(UserId, Channel, Type)`
`NotificationPreference` entity to avoid a column cartesian explosion) is recorded in ADR 0020, not built.

### 5.2 Where enforced — push only; in-app always persists

- **In-app inbox row + SignalR live toast:** **always** created/pushed, unconditionally (ADR 0015: "the in-app
  row may still always persist (source of truth), with the preference gating push/email fan-out"). The
  producing handlers are **unchanged** in their persist logic.
- **Web Push:** gated. `SendPushNotificationCommandHandler` reads the recipient's
  `Preferences.IsPushEnabled(notification.Type)` (one projected read) **before** loading devices/sending; if
  `false`, it returns early — no push, in-app untouched. Enforcing at the dispatcher (off the request path)
  keeps the check out of the write's hot path and in one place. **Default-on** means existing users, once they
  subscribe, receive push for every type until they mute one (standard opt-out semantics on top of the
  browser-permission opt-in gate).

### 5.3 The settings surface (owner-only; extends `/settings`)

`SettingsController` (`[Authorize]`, no id bound — `ICurrentUser`) gains **`GET/POST /settings/notifications`**:
per-type toggles (Friend requests, Friend accepted, Review likes, Comments) plus the **"Enable push on this
device"** subscribe control (§3.1). This **replaces the Phase-4 disabled "Notification preferences — arriving
with push (Phase 6)" placeholder** (`phase-4-...-design.md §8`). HTMX partial swap + the established scoped
`#settings-status` polite announce + focus-restore; anti-forgery + `social-write`. The toggles govern push;
copy makes clear in-app notifications always arrive.

---

## 6. Performance polish (ADR 0021 §perf)

(performance-agent + backend-agent; skills: `dotnet-performance`, `redis-caching`, `ef-core-data-access`.)
This pass **closes the accumulated `REVIEW_BACKLOG.md` perf/polish items** (enumerated in §6.7) and hits the
Core Web Vitals / Lighthouse targets (§6.6). Each item below is measured — **no speculative optimization**.

### 6.1 Response / static-asset compression (closes the headline backlog item)

`Program.cs` has **no** compression today (confirmed), so `site-*.js` (~184 KB, incl. the ~56 KB
`@microsoft/signalr`) + CSS serve uncompressed — a mobile-Lighthouse risk (backlog 2.3 + 3.5 Med, carried).
**Decision: compress the static bundle** — the dominant, **BREACH-safe** win (JS/CSS/fonts carry **no secret
and no reflected user input**). Two candidate mechanisms:
- **Preferred: `MapStaticAssets()`** (.NET 8+/10) replacing `UseStaticFiles()` for `wwwroot` — build-time
  **gzip + Brotli** precompression, strong `ETag`, and immutable `Cache-Control` for fingerprinted assets,
  with content-negotiated serving. **Caveat to verify:** the esbuild output must participate in the
  static-web-assets manifest, i.e. the `BuildFrontendAssets` MSBuild target must run **before** the
  static-asset manifest is computed on publish; if that ordering can't be guaranteed, fall back to →
- **Fallback: `AddResponseCompression` + `UseResponseCompression`** with the **Brotli + Gzip** providers,
  **scoped to static/compressible MIME types** (`text/css`, `text/javascript`/`application/javascript`,
  `image/svg+xml`, `application/manifest+json`, fonts). **`EnableForHttps`** is enabled **only for these
  static types** (no secrets); **dynamic HTML/HTMX-partial compression is left OFF** (or gated behind a
  security review) because a compressed HTML response that contains both the rotating anti-forgery token
  **and** reflected user input (reviews, display names) is the classic **BREACH** exposure. HTML partials are
  small; the static bundle is the real payload, so this scope captures ~all the benefit with zero BREACH risk.

**Middleware ordering (security-preserving):** `SecurityHeadersMiddleware` stays **upstream** of static
serving and of any OutputCache, so **every** response — compressed static asset, cached hit, or dynamic page —
is still stamped with the single authoritative CSP/`nosniff`/`Referrer-Policy`/`Permissions-Policy` header set
on each live request (the headers are set before `_next`, independent of whether a downstream endpoint serves a
cached/precompressed body). This is the exact ordering the Phase-2 backlog flagged for OutputCache, applied
here.

### 6.2 OutputCache — pull forward **only** behind measurement + a security-ordering review (accepted-deferred at Phase 2 exit)

The Phase-2 exit **deferred** OutputCache (the TMDB round-trip is already absorbed by `CachedTmdbClient`;
OutputCache would only save the cheap Razor render, and wiring is delicate against the rate limiter + the CSP
header). Phase 6's performance pass **re-measures**: if the Razor render on repeat anonymous rail/search/
Details hits is a **measured** cost (Lighthouse/TTFB signal), wire an `AddOutputCache`/`UseOutputCache`
`DiscoveryRails` policy — **short TTL ≤ the TMDB TTLs**, **`VaryByQuery("kind","media","q","page")`**, **cache
only anonymous, no-`Set-Cookie` responses**, tagged for invalidation — with a **security-agent ordering review**
(cached hits must still carry the live CSP header — guaranteed by the §6.1 ordering — and must not short-
circuit the `public-read` limiter in a way that lets a flood bypass TMDB-budget protection; since cache hits
don't spend the TMDB budget, serving them ahead of the limiter is acceptable). **If not measured to matter, it
stays deferred** — this design does not force it. Owner: performance-agent (+ security-agent).

### 6.3 Avatar serving — cheaper conditional-GET validator (closes backlog 4.1)

`LocalFileStorage.TryReadAsync`/`MediaController` currently read the whole avatar into a `byte[]` and compute
a SHA-256 content-hash `ETag` on **every** GET — including conditional GETs that 304. Fix: serve via
`PhysicalFileResult`/`FileStreamResult` and derive a **cheap weak validator** (length + last-write-time, or a
cached hash) so an `If-None-Match` **304s before** the read+rehash. The bucketed-expiry signed URL + cache
headers (ADR 0013 §2.4) are unchanged; this only makes the "cacheable avatar" goal fully real. Owner:
performance-agent + backend-agent.

### 6.4 Feed body — push the excerpt truncation into SQL (closes backlog 3.4)

`FeedItemProjection` pulls the full `Body` (`nvarchar(4000)`) DB→app per row then truncates in memory
(~80 KB/page → ~4 KB rendered), with a comment wrongly claiming `SUBSTRING` isn't translatable. Fix: project
`Body.Length <= 201 ? Body : Body.Substring(0, 201)` (EF's SQL Server provider **does** translate
`string.Substring`/`.Length`) so only the excerpt crosses the wire; fix the comment. **Feed only** — the
Details reviews list legitimately needs the full body. Measure first (marginal on co-located LocalDB). Owner:
backend-agent + performance-agent.

### 6.5 Bundle audit + SignalR lazy-load; motion/LCP/CLS; log redaction

- **SignalR out of the shared bundle (closes backlog 3.5):** `@microsoft/signalr` (~56 KB) rides `site-*.js`
  on **every** page though anonymous pages never open a hub. **Lazy-load** it via a dynamic `import()` gated
  on the presence of `#notification-unread-badge` (authed pages only), or split an authed-only chunk. Frontend-agent.
- **Motion / LCP / CLS (closes backlog 2.3 Lows):** trim over-budget motion (poster hover-zoom + entrance
  →≤300 ms / ≤350 ms); animate the LCP hero with **`transform` only** (no `opacity:0` start on the LCP
  element); match skeleton text-block heights to rendered lines (kill the ~3–5 px CLS on skeleton→content
  swap). Reduced-motion honored throughout. Frontend-agent + ux-agent; confirm at the Lighthouse gate.
- **Eager avatars above the fold (closes backlog 4.2 Low):** allow an `eager` preset for nav/header/settings
  avatar placements (no CLS either way — the box is reserved). performance-agent.
- **`CacheLog` query redaction (closes backlog 2.5 sec Low):** `CacheLog.ReadFailed/WriteFailed` log the full
  search cache key (embedding the normalized query) at Warning. Redact/hash the query portion (or log only the
  key prefix). backend-agent (`Infrastructure/Tmdb/CacheLog.cs`).
- **Notification-bell COUNT per authed render (backlog 3.5 Low, candidate):** the `NotificationBellViewComponent`
  runs one indexed unread `COUNT` on every authenticated page render. **Only if** authed TTFB is measured to
  matter: a short-TTL per-user `IDistributedCache` count (invalidated on create/mark-read), or render the badge
  empty and let `/notifications/unread-count` + SignalR seed it. Measure-gated; not forced.

### 6.6 Core Web Vitals / Lighthouse targets + measurement

- **Targets (WCAG AA + green CWV):** **LCP < 2.5 s**, **CLS < 0.1**, **INP < 200 ms**; Lighthouse **PWA
  installable = pass**; **Performance / Accessibility / Best-Practices / SEO ≥ 90** on **Home (`/discover`),
  a Details page, and the Feed (`/home`)**. No Critical/High from `/review-performance` or `/review-ui`.
- **Measured with:** Lighthouse (Chrome DevTools / `npx lighthouse` — free) on the three routes in a
  production-`Release` run; the DevTools **Application** panel for install + service-worker + offline; a
  Playwright trace for INP-sensitive interactions. All **free/local** — no paid perf service.

### 6.7 REVIEW_BACKLOG items Phase 6 closes (enumerated)

Phase 6 must triage `REVIEW_BACKLOG.md` so every remaining item is explicitly **fixed** or **accepted** (exit
criterion). This design commits to **closing**:

| Backlog item (source) | Phase-6 disposition |
|---|---|
| **No response compression** (2.3 last row; 3.5 Med, carried) | **CLOSED** — static-bundle compression (§6.1). |
| **SignalR (~56 KB) on every page** (3.5 Low/Med) | **CLOSED** — lazy-load gated on the badge (§6.5). |
| **Avatar ETag re-read+rehash on every GET incl. 304** (4.1 Low perf) | **CLOSED** — cheap validator + `PhysicalFile` (§6.3). |
| **Feed pulls full `Body` then truncates in memory** + wrong `SUBSTRING` comment (3.4 Low) | **CLOSED** — SQL-side truncation (§6.4). |
| **`CacheLog` logs full search key** (2.5 sec Low) | **CLOSED** — redact/hash the query (§6.5). |
| **Motion durations over budget; LCP hero opacity fade; skeleton CLS** (2.3 Lows) | **CLOSED** — motion/LCP/CLS polish (§6.5). |
| **Above-the-fold avatars use `lazy`** (4.2 Low) | **CLOSED** — eager preset (§6.5). |
| **`A3` `EnableRetryOnFailure` off** (1.2) | **ACCEPTED (stays OFF)** — the user-managed registration transaction forbids it unless wrapped in an execution strategy; irrelevant for the single-instance self-hosted/LocalDB profile (§8, ADR 0021). |
| **`OutputCache` on anonymous Discovery GETs** (2.3/2.4/2.5, accepted-deferred at Phase 2 exit) | **CONDITIONAL** — pulled forward **only if measured**, behind a security-ordering review (§6.2); else remains accepted-deferred. |
| **`ForwardedHeaders`/trusted-proxy for rate-limit partition** (2.5/4.1 deploy) | **ADDRESSED in the deployment PLAN** (plans-only) — enable before `UseRateLimiter` **iff** a reverse proxy is introduced (§8). |
| **Styled delete-confirm; one-way comments-collapse** (3.1/3.2 "Phase 6 polish") | **CLOSED** in the UI polish pass (§7) — CSP-safe `@alpinejs/csp` (frontend-agent). |
| **Notification-bell COUNT per render; `MarkAllRead` set-based** (3.5 Lows) | **CANDIDATE / ACCEPTED** — measure-gated (§6.5); implement only if TTFB/volume signals warrant. |

---

## 7. Accessibility + animation/design polish (scope; owned by ux-agent + frontend-agent)

The architecture doc **scopes** these (they gate at `/review-ui`) and hands execution to ux/frontend:
- **Accessibility pass:** full **keyboard walkthrough** of the app flow (landing → login → discover → search →
  details → review → feed → notifications → settings → push opt-in); **contrast audit** on the dark surfaces
  (WCAG AA, ≥ 4.5:1 text / 3:1 non-text); **`aria-live`** correctness on every dynamic region (reuse the
  established scoped polite-`#…-status` pattern from 2.4/3.x — the new push toast + update toast + settings
  status join it); **reduced-motion** audit (`prefers-reduced-motion` honored by every §6.5 animation).
- **Animation / design polish:** **View Transitions** between key pages (progressive-enhancement, reduced-
  motion-guarded, CSP-clean); staggered rails; consistent **empty / error / loading** states across the new
  surfaces (`/offline`, push-denied, settings); the styled delete-confirm + comments-collapse (§6.7). The new
  **`/offline`** page and the **update/permission toasts** must match the premium dark-glass system.

**No CSP impact** — View Transitions are a CSS/JS platform feature; all scripts remain bundled + `@alpinejs/csp`.

---

## 8. Deployment — PLANS-ONLY, free / local / self-hosted (ADR 0021)

**Nothing here is executed by an agent.** The runbook (`docs/deployment/runbook.md`, documentation-agent) is
written for the **human** to run locally; the existing `scripts/db-update.ps1` + `scripts/dev.ps1` are the
local helpers. **No Azure paid tier, no Docker, no cloud subscription** (ADR 0004). **devops-agent is not
used — backend-agent owns the scripts/config/`/health`.**

- **Database:** **SQL Server Express (free)** in "production", **LocalDB** in dev, **LocalDB `CinoraTest`** for
  integration tests — all free, no Docker (ADR 0002/0004). Connection string via **`ConnectionStrings__Default`**
  (env var / user-secrets), **never** in `appsettings.*.json`.
- **Secrets per environment:** **user-secrets (dev) / environment variables (prod)** only — the **VAPID private
  key** (`WebPush:PrivateKey`), **`FileStorage:UrlSigningKey`**, and Google OAuth creds. Linux env-var nesting
  uses `__` (`WebPush__PrivateKey`). appsettings holds only non-secrets (the VAPID **public** key may live in
  appsettings — it is non-secret). `WebPushOptions.ValidateOnStart()` **ON** (first consumer, Phase 6) so a
  misconfigured/absent key fails fast at boot in Production — **but push absence must degrade, not crash the
  app**: bind + validate the keys *when the push feature is enabled*, and treat an unconfigured VAPID as
  "push disabled" (log a startup Warning) rather than a fatal boot error, so a deployer without push still
  runs (mirrors the conditional Google-OAuth wiring in `Program.cs`).
- **Migrations — idempotent script, never auto-migrate:** regenerate **`artifacts/migrate.sql`** after the
  Phase-6 migration (`dotnet ef migrations script --idempotent --project src/Cinora.Infrastructure
  --startup-project src/Cinora.Web -o artifacts/migrate.sql`); apply it **manually** (or `scripts/db-update.ps1`
  in dev). The app **never** calls `Database.Migrate()` on startup (race + rollback hazard). The one Phase-6
  migration is **additive** (§9), so the script is a clean forward-only apply.
- **`/health`:** the existing anonymous `MapHealthChecks("/health")` DB-connectivity probe is unchanged
  (200 healthy / 503 unhealthy). Push/VAPID config is **not** a health dependency (absence degrades, §above).
- **Data-Protection key ring:** on a self-hosted single instance, **persist the DP key ring to a stable folder**
  (`App_Data/keys`, `PersistKeysToFileSystem`) so the auth cookie + anti-forgery tokens survive an app restart
  (otherwise a restart invalidates every session). A runbook item (backend-agent to wire; plans-only).
- **`EnableRetryOnFailure` (backlog A3):** **stays OFF** — the user-managed registration `IDbContextTransaction`
  is incompatible with SQL retry unless wrapped in `Database.CreateExecutionStrategy().ExecuteAsync(...)`
  (backlog CR6). For the single-instance local/Express target, transient faults are rare; leave OFF and
  document the coupling. (Re-enable only alongside the execution-strategy wrap if a flaky remote DB ever
  appears — not this profile.)
- **`ForwardedHeaders` (backlog 2.5/4.1):** the rate-limit partition keys on `RemoteIpAddress`; **iff** a
  reverse proxy ever fronts the app, add `UseForwardedHeaders` + a **trusted-proxy allow-list** **before**
  `UseRateLimiter` so clients don't collapse into one bucket. **Plans-only** — not wired for the direct-Kestrel
  local target.
- **Publish output:** `dotnet publish -c Release -o ./publish` — the `BuildFrontendAssets` MSBuild target runs
  `npm ci && npm run build`, so the publish output includes `wwwroot/dist/*` (hashed CSS/JS + `manifest.json`),
  **`wwwroot/sw.js`**, **`wwwroot/manifest.webmanifest`**, and **`wwwroot/icons/*`**. The runbook **verifies**
  those PWA artifacts are present in `./publish` (a common miss).
- **Environment-name exactness:** behavior keys off `Development` / `Testing` / `Production` **spelled exactly**
  (the Phase-1 startup Warning guard catches a typo like `Prod` that would leave diagnostics exposed). The
  runbook states `ASPNETCORE_ENVIRONMENT=Production` **verbatim**.

---

## 9. Migrations / schema deltas (for database-agent — do NOT write here)

**One additive migration** (`Phase6PushAndPreferences`), generated in `Cinora.Infrastructure` (startup project
`Cinora.Web`), applied via the reviewed idempotent SQL script / `db-update.ps1` — **never** auto-migrate.
`CinoraTest` gets it automatically; **`CinoraDev` (Express) apply is carried** with the standing
Express-unreachable caveat (PROGRESS open item).

| Change | On | Why | Notes |
|---|---|---|---|
| **4 × `bit NOT NULL DEFAULT 1`** columns (`PushFriendRequests`, `PushFriendAccepted`, `PushReviewLikes`, `PushComments`) | `Users` (owned `NotificationPreferences`) | The notification-preference model (§5) — **no new table, no join** | Additive; default-on backfills existing users to opt-in. EF **owned-type** mapping. |
| **`EndpointHash`** column (`char(64)`/`binary(32)`) + **unique index `(UserId, EndpointHash)`** | `Device` | Subscribe **upsert/dedup** (§3.3) — the raw `nvarchar(2048)` endpoint can't be indexed (>900-byte key) | Additive. For **existing** Device rows (none in prod yet — push is new) backfill the hash or apply before any subscription exists. |
| **(optional) filtered/covering index** for the like-path dedup `AnyAsync` (`Notifications(RecipientUserId, ActorUserId, TargetId, Type)`) | `Notification` | backlog 3.5 Low — only if the like path proves hot | **Measure first**; likely deferred. |

- No change to `Notification`, `Review`, `Watchlist`, `Friend`, `Movie`. `Device`'s existing `(UserId)` index
  stays (device lookups on send).
- **Honest schema flags:** the preference model is **push-only, per-type** (§5.1) — a future **email/in-app-mute**
  channel would migrate to a `NotificationPreference(UserId, Channel, Type, Enabled)` entity (recorded, not
  built). Dead `Device` rows are pruned on-send (404/410); an optional TTL sweep is a later Hangfire chore.

---

## 10. Options additions (bind Options — never inject `IConfiguration`)

- **`WebPushOptions`** (`Cinora.Infrastructure/Options/`): `SectionName = "WebPush"`; `Subject` (a `mailto:`
  or origin URL, VAPID requirement), `PublicKey` (base64url, non-secret), `PrivateKey` (base64url, **secret →
  user-secrets/env**), plus a nested/`PushOptions` for `DefaultTtlSeconds`/`Urgency`. `ValidateUsingDataAnnotations()`;
  `ValidateOnStart()` **ON** only when push is enabled (see §8 degrade-not-crash). Bound in `AddInfrastructure`;
  `WebPushSender` + `SendPushNotificationCommandHandler` read `IOptions<WebPushOptions>` — **never**
  `IConfiguration`.
- No change to `TmdbOptions`/`AiOptions`/`CacheOptions`/`FileStorageOptions`/`GoogleAuthOptions`. The SW/PWA
  needs no server Options (static assets + build-time injection).

---

## 11. Key risks and product decisions

**Risks baked into the design:**
- **Stale assets after a deploy** — mitigated by keying the SW `VERSION` + precache list to the content-hashed
  `manifest.json` (§2.2) so every bundle change rotates the SW and purges old caches; `sw.js`/manifest served
  `no-cache` so updates are detected. Residual: a client that never revisits keeps the old SW until it does
  (inherent to SW); the update toast prompts a refresh when it does.
- **Caching a personalized page offline (privacy)** — mitigated by `no-store` on authenticated responses + the
  SW refusing to cache `no-store` (§2.3). Residual: `/offline` (not the real page) shows for authed routes
  offline — an accepted, deliberate trade-off.
- **Push endpoints are slow/flaky** — kept **off the request critical path** (`IPushDispatch`, own scope,
  fire-and-forget; Hangfire when it lands) and **best-effort** (a failure never fails the write; the inbox is
  authoritative). Residual: a dropped push is a missed out-of-app nudge, never a missed notification.
- **Dead subscriptions** — pruned aggressively on 404/410 (§3.6); an optional TTL sweep later.
- **BREACH via response compression** — avoided by scoping compression to **static, secret-free** assets and
  leaving dynamic-HTML compression off/review-gated (§6.1).
- **VAPID key management** — the private key gates who can send push as Cinora (moderate sensitivity). Prod key
  in user-secrets/env; **absence degrades push to off, never crashes boot** (§8). Rotating VAPID keys
  invalidates existing subscriptions (clients re-subscribe) — documented.
- **New MIT dependency (the WebPush library)** — license verified at add-time + pinned in CPM; a BCL hand-roll
  is the recorded fallback (§3.2). This is the one new server package Phase 6 adds.

**Genuine PRODUCT / UX decisions (defer to the user or ux-agent — NOT architecture):**
- **Which events push by default** — design ships **all four push-on by default** (opt-out per type, on top of
  the browser opt-in). A "quieter" default (e.g. friend-events only) is a product call.
- **Where to place the push opt-in prompt** — `/settings/notifications` (shipped) vs. also a contextual
  "Notify me" on a friend request. UX call.
- **Offline scope** — public reading offline is shipped; **offline write queuing (Background Sync)** is
  deferred (YAGNI) — pull in only if users ask.
- **Notification-preference granularity** — per-type push (shipped) vs. a future per-channel matrix (deferred
  behind the email trigger, §9).
- **Poster offline caching size / eviction** — the LRU cap (§2.2) is a tunable; large-library users vs. storage.
- **View Transitions scope** — which page pairs animate — a UX/motion call (§7).

---

## 12. Phase 6 milestone build order (delegable)

Ordered milestones for the orchestrator. Each states the owning agent(s), the deliverable, the exact `Verify`
command/acceptance, and the review gates (`/review-architecture`, `/review-code`, `/review-security`,
`/review-performance`, `/review-ui` — automatic after every milestone and at phase end). All commands run from
the repo root. Tests are **xUnit `Assert` + NSubstitute** (no FluentAssertions), integration via
`WebApplicationFactory` against **LocalDB `CinoraTest`**, and **Playwright E2E (free)** for install/offline/push
where warranted. **No agent deploys or runs `git`.**

> **Dependency order:** 6.1 (PWA foundation — the SW must exist first) → 6.2 (Web Push — needs the SW's
> `push`/`notificationclick` handlers) → 6.3 (notification preferences — gates 6.2's dispatch) → 6.4
> (performance polish) → 6.5 (accessibility + animation/design polish) → 6.6 (deployment readiness, plans-only)
> → Phase-6 exit (all five review loops clean + E2E green + backlog triaged).

### 6.1 — PWA foundation: manifest + service worker + offline + versioned update
- **Owner:** pwa-agent + frontend-agent (+ architecture-agent sign-off on the esbuild-`sw.ts` integration and
  the cache-keying, ADR 0019).
- **Deliverable:** `manifest.webmanifest` + icons (192/512/maskable); `Scripts/sw.ts` → `wwwroot/sw.js`
  (root scope) via a **second `build.mjs` entry** with `__SW_VERSION__`/`__PRECACHE__` injected from
  `manifest.json`; SW registration + update toast in `site.ts`; `PwaController` `[AllowAnonymous] GET /offline`
  + the `no-store`-on-authenticated header middleware; `no-cache` on `/sw.js` + `/manifest.webmanifest`.
- **Verify:**
  ```
  npm run build --prefix src/Cinora.Web
  dotnet build Cinora.sln -c Release
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  In Chrome DevTools **Application** panel against a `Release` run: manifest is valid + **installable**; `sw.js`
  registers at scope `/`; **offline** (DevTools "Offline") on a previously-visited `/discover` serves from cache
  and an uncached route serves **`/offline`**; an authenticated `/home` shows `/offline` offline (not cached);
  a rebuilt bundle produces a **new SW version** and the **update toast** → refresh adopts it. Confirm
  `/sw.js` + `/manifest.webmanifest` carry `Cache-Control: no-cache`. **CSP unchanged** (grep
  `SecurityHeadersMiddleware` — no diff). Then `/review-ui`, `/review-architecture`, `/review-code`,
  `/review-security` (the `no-store`/offline-scope privacy check).

### 6.2 — Web Push: VAPID + `IPushSender`/`IPushDispatch` ports + Device upsert + SW push handlers + subscribe UX
- **Owner:** pwa-agent + backend-agent + security-agent (the VAPID/subscribe/anti-forgery gate) + architecture-agent
  sign-off on the port boundary + the ADR-0011 seam reuse (ADR 0020).
- **Deliverable:** `WebPushOptions` (+ user-secrets VAPID; degrade-if-absent); `IPushSender` + `WebPushSender`
  (MIT WebPush lib, pinned in CPM); `IPushDispatch` + `InlinePushDispatch` (own scope, best-effort);
  `RegisterDeviceCommand` (upsert by `EndpointHash`) + `PushController` (`/push/subscribe`, `/push/unsubscribe`,
  `/push/public-key`, `[Authorize]` + `social-write`); `SendPushNotificationCommand` + handler (load devices →
  build payload + `NotificationDeepLink` → send → **prune 404/410**); the `RealtimeNotificationDispatcher` seam
  extended to enqueue push; `sw.ts` `push` + `notificationclick` handlers; the `NotificationDeepLink` DRY
  consolidation. **`Device` `EndpointHash` migration is 6.3's migration or split here — see §9.**
- **Verify (mock-first + one real-browser round-trip):**
  ```
  dotnet build Cinora.sln -c Release
  dotnet test tests/Cinora.Application.Tests/Cinora.Application.Tests.csproj
  dotnet test tests/Cinora.Infrastructure.Tests/Cinora.Infrastructure.Tests.csproj
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  Unit: `SendPushNotificationCommandHandler` skips when the preference is off; deletes a Device on a faked
  `IPushSender` `Gone`; builds the correct `data.url` per type (via `NotificationDeepLink`); `RegisterDeviceCommand`
  **upserts** (second subscribe with the same endpoint updates, not duplicates). Integration: `/push/subscribe`
  requires auth + anti-forgery; a subscription lands as a `Device` for the current user. **Real browser
  round-trip (exit criterion):** in a real browser, enable push → have another user like your review → the OS
  notification appears with the correct deep link → clicking focuses/opens the Details page. **CSP unchanged.**
  Then `/review-security` (VAPID/subscribe/anti-forgery), `/review-architecture` (port placement + seam reuse),
  `/review-code`.

### 6.3 — Notification preferences (un-defer ADR 0015): owned VO + migration + enforcement + settings surface
- **Owner:** backend-agent + database-agent (the owned-type migration) + frontend-agent + ux-agent (the surface).
- **Deliverable:** `NotificationPreferences` owned VO + `User.UpdateNotificationPreferences`; the EF owned-type
  mapping → 4 `bit` columns (+ the `Device.EndpointHash` column/index if not landed in 6.2) in one
  `Phase6PushAndPreferences` migration; `GetNotificationSettingsQuery` + `UpdateNotificationPreferencesCommand`;
  `SettingsController` `GET/POST /settings/notifications` (replacing the Phase-4 placeholder); the dispatcher's
  preference check (§5.2).
- **Verify:**
  ```
  dotnet ef migrations add Phase6PushAndPreferences --project src/Cinora.Infrastructure --startup-project src/Cinora.Web
  dotnet ef migrations script --idempotent --project src/Cinora.Infrastructure --startup-project src/Cinora.Web -o artifacts/migrate.sql
  dotnet build Cinora.sln -c Release
  dotnet test tests/Cinora.Application.Tests/Cinora.Application.Tests.csproj
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  The migration is **additive** (4 default-`1` columns + Device index, no drops/renames); `UpdateNotificationPreferencesCommand`
  operates on `ICurrentUser` only (no id bound — no cross-user edit); a muted type is **not** pushed but **is**
  still in the inbox (unit test on the dispatcher). Then `/review-ui`, `/review-code`, `/review-architecture`.

### 6.4 — Performance polish (bundle, compression, avatar ETag, feed truncation, log redaction, measure-gated OutputCache)
- **Owner:** performance-agent + backend-agent + frontend-agent (bundle/motion).
- **Deliverable:** static-asset compression (§6.1); SignalR lazy-load (§6.5); avatar cheap-validator serving
  (§6.3); feed SQL truncation (§6.4); `CacheLog` redaction (§6.5); motion/LCP/CLS + eager-avatar tweaks (§6.5);
  OutputCache **only if** the measurement warrants it (§6.2). Update `REVIEW_BACKLOG.md` dispositions (§6.7) —
  **orchestrator edits the backlog, not this milestone**.
- **Verify:**
  ```
  npm run build --prefix src/Cinora.Web
  dotnet build Cinora.sln -c Release
  dotnet test Cinora.sln -c Release
  npx lighthouse https://localhost:<port>/discover --preset=desktop --view   # + a Details page + /home
  ```
  Compressed `site-*.js`/CSS served with `Content-Encoding: br`/`gzip` + the **live CSP header still present**;
  a conditional avatar GET returns **304 without a full re-read**; the feed query's SQL truncates server-side
  (EF log shows `SUBSTRING`); Lighthouse meets §6.6 targets; full suite green (TWAE on). Then `/review-performance`,
  `/review-security` (compression scope + any OutputCache ordering), `/review-code`.

### 6.5 — Accessibility + animation/design polish
- **Owner:** ux-agent + frontend-agent (+ security-agent final CSP glance — must stay unchanged).
- **Deliverable:** the a11y pass (keyboard walkthrough, contrast, `aria-live`, reduced-motion); View Transitions;
  consistent empty/error/loading states incl. `/offline` + push-denied + settings; styled delete-confirm +
  comments-collapse (backlog 3.1/3.2).
- **Verify:**
  ```
  npm run build --prefix src/Cinora.Web
  dotnet build Cinora.sln -c Release
  npx playwright test   # keyboard + dark-mode + reduced-motion E2E (free)
  ```
  A full keyboard traversal reaches every control (no trap); contrast ≥ AA on the new surfaces; dynamic regions
  announce once (no flooding); `prefers-reduced-motion` disables the §6.5 animations; **CSP unchanged**. Then
  `/review-ui` to a clean exit.

### 6.6 — Deployment readiness (PLANS-ONLY) + Phase-6 exit
- **Owner:** backend-agent (scripts/config/`/health`/DP-key persistence — devops-agent NOT used) +
  documentation-agent (the runbook + final ADRs).
- **Deliverable:** `docs/deployment/runbook.md` (SQL Express target, env/user-secrets incl. VAPID, the
  idempotent `artifacts/migrate.sql` flow, DP key-ring persistence, `ForwardedHeaders`-if-proxied, publish +
  PWA-artifact verification, `ASPNETCORE_ENVIRONMENT=Production` verbatim, `/health`); README updates; the
  Playwright E2E suite covering **login, search, review, watchlist, notification** flows.
- **Verify (build/test/publish only — NOT deployed):**
  ```
  dotnet build Cinora.sln -c Release
  dotnet test  Cinora.sln -c Release
  dotnet publish src/Cinora.Web -c Release -o ./publish
  ```
  `./publish` contains `wwwroot/{sw.js,manifest.webmanifest,icons/*,dist/*}`; the E2E suite is green; the runbook
  is reviewed (read-through, not executed). **Phase-6 exit:** all five review loops clean (no Critical/High;
  WCAG AA); the real-browser push round-trip (6.2) verified; `REVIEW_BACKLOG.md` triaged to fixed-or-accepted.
  **No deploy, no `git`.**

---

_Design-only document authored 2026-07-03 against the Phase 1–4 code (verified: `Device` entity is Web-Push
transport with `Register`/`Seen` and no `EndpointHash`/unique-endpoint index; `Notification` +
`NotificationType` (4 values); the ADR-0011 `RealtimeNotificationDispatcher` post-commit best-effort seam +
`IRealtimeNotifier`; `SecurityHeadersMiddleware` strict CSP `default-src 'self'` … `img-src 'self'
https://image.tmdb.org data:`; `build.mjs` esbuild + `manifest.json` + `IAssetManifest`; `Program.cs` has **no**
`MapStaticAssets`/`AddResponseCompression`/`AddOutputCache`; `SettingsController` owner-only via `ICurrentUser`;
the notification deep-link grammar duplicated in `_NotificationItem.cshtml`; no `sw.js`/`manifest.webmanifest`/
`/offline` exist). No application code written; no build/test run._
