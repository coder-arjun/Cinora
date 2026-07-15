# ADR 0020 — Web Push (Self-Generated VAPID) over the `Device` Entity, and the Notification-Preferences Model

- **Status:** Accepted
- **Date:** 2026-07-03
- **Phase:** 6 (Polish & Deployment), Milestone 6.2 (Web Push) + 6.3 (notification preferences)
- **Deciders:** architecture-agent (pre-implementation design), backend-agent + security-agent (VAPID/subscribe
  path), pwa-agent (client), orchestrator (ratify); **user to ratify the push-default product call**
- **Supersedes:** the deferral in **ADR 0015** (Notification preferences deferred to Phase 6) — un-deferred here

## Context

Phase 6 adds **out-of-app reach** for the same social events SignalR already delivers **in-app** (ADR 0011):
reach a user whose Cinora tab is closed. The spec/skill name **Web Push with self-generated VAPID keys (free)**
and a **`Device`** entity to store subscriptions — the entity already exists (`Register(userId, endpoint,
p256dh, auth)`, `Seen()`, bounded columns), created as Phase-6 groundwork in Phase 1. The push send is a slow,
flaky, external call, so it must stay **off the request critical path** and be **best-effort** — the in-app
inbox row remains the source of truth (the ADR-0011 N2 contract). Constraints:

1. **Free/local-only, no Docker, no subscriptions (ADR 0004).** No paid push gateway, no Azure Notification
   Hubs. Web Push with **self-generated VAPID** keys is free and needs no third party — the browser's push
   service (FCM/Mozilla/Apple) is contacted **by the app server**, not by the page, using **our own** keys.
2. **No CSP widening.** The `PushManager.subscribe` call, push receipt, and `showNotification` are
   **browser-native** (not page network requests); the subscribe `POST` is **same-origin** (`connect-src 'self'`).
   No third-party origin the page connects to ⇒ **no CSP change**.
3. **Dependency rule (ADR 0001) + reuse the ADR-0011 seam.** The port lives in Application; the encryption/VAPID
   adapter in Infrastructure; the fan-out reuses the existing post-commit best-effort dispatch, not a new one.
4. **Un-defer ADR 0015.** Notification preferences were deferred to "where push lands and a delivery channel
   exists to govern" — that is now. The recorded future shape must be realized correctly for push.
5. **New dependency discipline.** MediatR/FluentAssertions/ImageSharp were banned for **commercial** licensing;
   a **free MIT** OSS library is permitted (Serilog, esbuild, `@microsoft/signalr` all ship). The Web Push
   library must be MIT, license-verified, and CPM-pinned.

## Decision

**Send Web Push behind an `IPushSender` Application port (adapter over a free MIT Web Push library + self-VAPID
in Infrastructure); store subscriptions by upsert on the existing `Device` entity; fan out from the ADR-0011
post-commit best-effort seam via an `IPushDispatch` port (inline-scoped now, Hangfire-swappable later); prune
dead endpoints on 404/410; resolve notification-click deep links from a single shared `NotificationDeepLink`
grammar; and model notification preferences as a per-type push opt-out `NotificationPreferences` owned value
object on `User` (columns, no new entity), enforced in the push dispatcher while the in-app row always persists.
No CSP change.**

### 1. Subscribe flow (opt-in, contextual) → upsert on `Device`

- Permission is requested **only after a deliberate user gesture** (`/settings/notifications` "Enable
  notifications"), never on page load. `pushManager.subscribe({ userVisibleOnly: true, applicationServerKey })`
  with the VAPID **public** key from `GET /push/public-key`. Progressive-enhancement-gated on
  `serviceWorker`/`PushManager` support; **degrades to in-app-only** when unsupported/denied.
- The `PushSubscription` is `POST`ed to `/push/subscribe` (same-origin `fetch` + the `RequestVerificationToken`
  anti-forgery header). `PushController` (`[Authorize]`, `social-write` rate limit) dispatches
  `RegisterDeviceCommand(endpoint, p256dh, auth)` for the **server-resolved** current user (ADR 0009 — no id
  from the client). It **upserts** by `(UserId, EndpointHash)`: `EndpointHash` = SHA-256 of the endpoint (BCL,
  computed in `Device.Register` — no external dep, no dependency-rule breach), with a **unique index
  `(UserId, EndpointHash)`** (the raw `nvarchar(2048)` endpoint exceeds SQL Server's 900-byte index key limit).
  One subscription per Device, many Devices per user.

### 2. `IPushSender` port + free MIT adapter (self-VAPID)

```csharp
public interface IPushSender   // Application
{
    Task<PushSendResult> SendAsync(PushSubscription sub, PushPayload payload, CancellationToken ct);
}
public enum PushSendResult { Delivered, Gone, TransientFailure }   // Gone = 404/410 → prune
```

`WebPushSender : IPushSender` (Infrastructure) wraps a **free MIT** Web Push .NET library (e.g. the `WebPush`
package by web-push-libs, or `Lib.Net.Http.WebPush`) implementing **RFC 8291** (`aes128gcm` payload encryption)
and **RFC 8292** (VAPID `Authorization`). VAPID material comes from `WebPushOptions` (bound; private key from
user-secrets/env — **never** `IConfiguration`, never appsettings). The library is license-verified at add-time
and **pinned in `Directory.Packages.props`**. The port speaks Application primitives — never the SDK type or a
`Device` entity on the wire.

### 3. Fan-out reuses the ADR-0011 seam; off the critical path

- The producing handlers already co-persist the `Notification` row then call the shared
  `RealtimeNotificationDispatcher` for the SignalR push. Phase 6 **extends that one seam** to also call
  **`IPushDispatch.Enqueue(notification.Id)`** after the SignalR push — the four handlers inject `IPushDispatch`
  and pass it (DRY-preserving; the alternative — a second explicit call per handler — is noted). Enqueue is
  **after commit** (the id exists) and **best-effort** (a push failure never fails the write; the inbox is
  authoritative — N2/ADR-0011).
- **`IPushDispatch` has two free adapters (ADR-0004 "port stays, free provider ships"):**
  **`InlinePushDispatch`** (ships now) runs the send on a background `Task` in its **own DI scope**
  (`IServiceScopeFactory`) — off the request thread, never touching the request's disposed scope;
  **`HangfirePushDispatch`** (drop-in when Hangfire lands, Phase 5) enqueues the same command durably/retried.
- **`SendPushNotificationCommandHandler`** (Application): load the notification; **check the recipient's per-type
  push preference (§4) and return early if muted** — the in-app row still stands; else load the recipient's
  `Device`s, build the `PushPayload` (title/body from the message + `data.url` from `NotificationDeepLink`, reusing
  the `NotificationProjection` enriched coords), `IPushSender.SendAsync` per device, and **delete any Device that
  returns `Gone` (404/410)**.

### 4. Notification-preferences model — owned VO on `User` (un-defers ADR 0015)

- `NotificationPreferences` **owned value object** on `User` — four `bool`s (`PushFriendRequests`,
  `PushFriendAccepted`, `PushReviewLikes`, `PushComments`, default **true**) + `IsPushEnabled(NotificationType)`
  + `User.UpdateNotificationPreferences(...)`. EF **owned-type** mapping ⇒ four `bit NOT NULL DEFAULT 1` columns
  on the **`Users`** table — **no new table, no join, additive migration**. This is ADR-0015's "small set of
  per-event opt-in flags owned by the user" option, chosen over a dedicated `NotificationPreference` entity
  because there is **one channel now** (push; in-app always-on) and **four** event types — a join per push would
  be waste, and YAGNI applies to architecture.
- **Enforcement is push-only:** the in-app inbox row + SignalR live toast are **always** delivered
  (ADR-0015 "the in-app row may still always persist, with the preference gating push/email fan-out"); only the
  Web Push fan-out is gated, in `SendPushNotificationCommandHandler`, off the request path. Default-on = standard
  opt-out on top of the browser-permission opt-in.
- **Surface:** `SettingsController` gains owner-only `GET/POST /settings/notifications` (per-type toggles +
  the device subscribe control), replacing the Phase-4 "arriving with push" placeholder.

### 5. Deep links + cleanup

- **`NotificationDeepLink.Resolve(type, reviewCoords, actorUserId)`** (Application) single-sources the route
  grammar currently **duplicated inline** in `_NotificationItem.cshtml` (`ReviewLiked/CommentAdded →
  /discover/title/{media}/{tmdbId}`, `FriendRequest → /friends`, `FriendAccepted → /users/{actor}`); the inbox
  card and the push `data.url` now agree.
- **`notificationclick`** (in the ADR-0019 worker): `clients.matchAll` → **focus an existing window** and
  navigate to `data.url`, else `openWindow(data.url)`; never blindly opens a tab.
- **Cleanup:** **404/410 → delete the Device** on send (primary, always-on, self-healing); an optional
  `LastSeenUtc`-TTL sweep is a later Hangfire chore.

## Consequences

**Positive**
- Free, no-Docker, no-subscription out-of-app reach that satisfies the exit criterion (a verified real-browser
  round-trip), behind a swappable port.
- **Zero CSP change** — subscribe is same-origin; push/`showNotification`/`subscribe` are browser-native with
  our own VAPID; no third-party origin the page connects to.
- Dependency rule intact and the ADR-0011 seam reused, not duplicated — push is off the write's critical path
  and cannot corrupt persistence.
- ADR 0015 realized correctly against a real channel: preferences gate push, the inbox stays authoritative,
  additive migration, no dead config.
- Hangfire-ready without hard-depending on it (Phase 5): the inline dispatcher ships now; the Hangfire adapter
  is a one-line DI swap.

**Negative / accepted**
- **One new server dependency** (a MIT Web Push library) — accepted (license-verified, CPM-pinned); a BCL
  hand-roll (RFC 8291/8292 over `ECDiffieHellman`/`HKDF`/`AesGcm`/`ECDsa`, all in .NET 10) is the recorded
  zero-dependency fallback if the license ever fails re-verification.
- The `Device` gains an `EndpointHash` column + unique index (additive) for race-safe upsert.
- Preferences are **push-only, per-type** — a future email/in-app-mute channel migrates to a
  `NotificationPreference(UserId, Channel, Type, Enabled)` entity (recorded, not built) to avoid a column
  cartesian explosion.
- VAPID private-key management + a rotate invalidating existing subscriptions (clients re-subscribe) — documented
  in the runbook; absence **degrades push to off, never crashes boot**.

## Alternatives considered

1. **Paid push gateway / Azure Notification Hubs.** Rejected — paid/cloud, violates ADR 0004. Self-VAPID is free.
2. **Hand-roll RFC 8291/8292 over the BCL as the primary.** Rejected as primary — the encryption is fiddly and a
   vetted MIT library is lower-risk; kept as the fallback (the BCL has every primitive).
3. **A dedicated `NotificationPreference` entity now.** Rejected for Phase 6 — one channel + four types makes a
   join-per-push waste; the owned VO (columns) is simpler and additive. The entity is the documented upgrade path
   when a second channel arrives.
4. **Gate the in-app notification/SignalR toast on the preference too.** Rejected — ADR 0015 scopes the preference
   to **push/email** fan-out; the in-app inbox is the always-on source of truth. A future in-app mute can reuse
   the model (YAGNI now).
5. **One subscription per user.** Rejected — users have phones + desktops; store **per Device** (skill).
6. **Send push on the request thread.** Rejected — push endpoints are slow/flaky; the `IPushDispatch` seam keeps
   it off the critical path, best-effort.
7. **Push logic added directly to the four producing handlers.** Rejected — duplicates the fan-out; extending the
   single `RealtimeNotificationDispatcher` seam keeps it DRY (mirrors why the SignalR push is centralized there).

## Related
- ADR 0011 (the post-commit best-effort dispatch seam + `IRealtimeNotifier` this reuses; `Device`/Web-Push
  named as Phase-6 groundwork), ADR 0015 (**superseded** — preferences un-deferred here), ADR 0009
  (`ICurrentUser` — the subscribing user is server-resolved), ADR 0004 (free/local-only — self-VAPID, no paid
  gateway), ADR 0019 (the service worker hosting the `push`/`notificationclick` handlers), ADR 0021 (VAPID
  secret handling + the additive migration in the deployment plan).
- `docs/architecture/phase-6-polish-deployment-design.md` §3 (Web Push), §5 (preferences), §4 (no-CSP-widening),
  §9 (migration).
- Skills: `.claude/skills/web-push-notifications/SKILL.md`, `.claude/skills/pwa-service-worker/SKILL.md`,
  `.claude/skills/signalr-realtime/SKILL.md`, `.claude/skills/hangfire-background-jobs/SKILL.md`,
  `.claude/skills/security-hardening/SKILL.md`, `.claude/skills/dotnet-configuration-options/SKILL.md`.

---

_Design-only ADR authored 2026-07-03 (verified: `Device` entity carries `Endpoint`/`P256dhKey`/`AuthSecret`/
`Register`/`Seen` with a `(UserId)` index and **no** `EndpointHash`/unique-endpoint index; `Notification` +
4-value `NotificationType`; the ADR-0011 `RealtimeNotificationDispatcher` best-effort seam; `SettingsController`
owner-only via `ICurrentUser`; the deep-link grammar duplicated in `_NotificationItem.cshtml`; no `IPushSender`/
`WebPushOptions`/push types exist in `src/`; Hangfire not yet present). No application code written._

---

_Verification note (2026-07-06) — verified against shipped Milestones 6.2 (Web Push) + 6.3 (notification
preferences): `IPushSender` (Application) + `WebPushSender` over the free MIT `WebPush` package (pinned in
`Directory.Packages.props`, Infrastructure-only) registered in `src/Cinora.Infrastructure/DependencyInjection.cs`;
`WebPushOptions` (`Subject`/`PublicKey`/`PrivateKey`, `SectionName = "WebPush"`) — **all fields optional** so
absence degrades push to off (`IsConfigured == false` → sender no-ops), never a boot crash (design §8);
`Phase6PushAndPreferences` additive migration ships the 4 `bit NOT NULL DEFAULT 1` preference columns on `Users`
+ `Devices.EndpointHash` + the unique `IX_Devices_UserId_EndpointHash` upsert/dedup index. VAPID secret handling
and the additive-migration pre-apply `Devices` check are documented in `docs/deployment/runbook.md` §3.1 + §5.
The **real-browser push round-trip** and the **push-permission/settings** checks are carried as manual sign-off
(runbook §10). No accepted Decision text changed._

Last verified against code: 2026-07-06
