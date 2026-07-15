# ADR 0011 — Self-Hosted SignalR for Realtime Notifications (no Azure SignalR, no backplane)

- **Status:** Accepted
- **Date:** 2026-07-03
- **Phase:** 3 (Reviews & Social), Milestone 3.5 (Notifications + realtime)
- **Deciders:** architecture-agent (design), backend-agent + security-agent (consulted), orchestrator (ratify)

## Context

Phase 3 delivers in-app notifications ("your review was liked/commented," "you have a friend request/it was
accepted"). Users expect these to appear **live** — a new-notification toast and an unread-count badge that
updates without a page reload. The `signalr-realtime` skill is the house pattern for latency-sensitive,
user-visible events, and its default registration example uses an **Azure/Redis backplane**
(`AddSignalR().AddStackExchangeRedis(...)`).

Two hard constraints bind the choice (ADR 0004, `CLAUDE.md`): **FREE-ONLY / no paid services** (so **no Azure
SignalR**) and **LOCAL-ONLY / no Docker** (so no containerized Redis). The app runs as a single self-hosted
instance. Realtime must be delivered at zero cost with the tools already in the box.

## Decision

**Host SignalR in-process (self-hosted) with `AddSignalR()` and NO backplane. Expose one strongly-typed
`NotificationHub`, authenticate it with the existing Identity cookie (fail-closed), and fan out to a
per-user group. Push behind an Application port so handlers stay Clean-Architecture-clean.**

### 1. Hosting & registration (free, no backplane)

- `builder.Services.AddSignalR();` — nothing else. **No `AddStackExchangeRedis`, no Azure SignalR.** A
  backplane only matters with ≥2 server instances; Cinora runs single-instance locally (ADR 0004). The
  backplane seam is a one-line registration change if horizontal scale is ever needed — recorded, not built
  (YAGNI, mirroring the cache/AI/storage "port stays, only free provider ships" stance).
- The SignalR browser client (`@microsoft/signalr`, MIT/free) is added to `package.json` and **bundled
  locally via esbuild** — no CDN — so `script-src 'self'` is unaffected. The hub connects same-origin
  (`wss://<host>/hubs/notifications`), which `connect-src 'self'` already permits; **no CSP change** is
  required in Phase 3.

### 2. Hub, auth, and group-per-user

```csharp
public interface INotificationClient           // strongly-typed → renames break the build, not production
{
    Task ReceiveNotification(NotificationDto notification);
    Task UnreadCountChanged(int unreadCount);
}

[Authorize]                                     // fail-closed: an anonymous connection must never join a group
public sealed class NotificationHub : Hub<INotificationClient>
{
    public override async Task OnConnectedAsync()
    {
        // Context.UserIdentifier is the Identity cookie's NameIdentifier claim — the SAME Guid ICurrentUser
        // resolves (ADR 0009), so server-side pushes and request-time identity agree.
        await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{Context.UserIdentifier}");
        await base.OnConnectedAsync();
    }
}
```

- `[Authorize]` on the hub is mandatory — it is the fail-closed contract applied to the socket. Without it
  `Context.UserIdentifier` is null and every group collapses to `user-`. The Identity cookie flows to the hub
  automatically because the connection is same-origin.
- **Group-per-user** (`user-{userId}`) fans a push out to every open tab/device the user has.

### 3. Push behind an Application port (dependency-rule-correct placement)

- `Cinora.Application/Common/Interfaces/IRealtimeNotifier.cs` defines the port:
  ```csharp
  public interface IRealtimeNotifier
  {
      Task NotifyAsync(Guid recipientUserId, NotificationDto notification, CancellationToken ct);
      Task UnreadCountChangedAsync(Guid recipientUserId, int unreadCount, CancellationToken ct);
  }
  ```
  with an Application-owned `NotificationDto` (never a Domain entity or EF type on the wire — the skill's
  "map to DTOs" rule).
- **The adapter lives in Web, not Infrastructure.** `SignalRRealtimeNotifier : IRealtimeNotifier` wraps
  `IHubContext<NotificationHub, INotificationClient>`. The hub and `INotificationClient` are hosting/transport
  types that live in `Cinora.Web`; Infrastructure cannot reference Web (the dependency rule), so the adapter
  is registered at the composition root: `builder.Services.AddScoped<IRealtimeNotifier, SignalRRealtimeNotifier>()`.
  This is the same composition-root pattern by which the app supplies Application ports — legitimate, and it
  keeps Infrastructure out of the realtime transport entirely.
- A triggering command handler (like/comment/friend) **persists the `Notification` row via `IAppDbContext` in
  its own `SaveChangesAsync`**, then calls `IRealtimeNotifier.NotifyAsync(...)` **after the save, best-effort**
  (wrapped in try/catch → `LogWarning`; a push failure must never roll back or fail the write). Persistence is
  the source of truth (the inbox always shows it); realtime is a live accelerator over it. No `ISender`
  inside a handler.

### 4. Scope: what is built vs stubbed in Phase 3

- **Fully built:** `NotificationHub`, cookie auth, group-per-user, the `IRealtimeNotifier` port + Web adapter,
  live new-notification toast, live unread-count badge, JS client with `.withAutomaticReconnect(...)` and an
  `onclose` "refresh to reconnect" affordance.
- **Deferred / stubbed:** a `FeedHub` for live activity-feed append (the feed refreshes on navigation / pull;
  live append is a later polish, not Phase-3 critical); the **Redis/Azure backplane** (single instance);
  **Web Push / `Device`** delivery for tab-closed users (Phase 6 — the `Device` entity comment already says
  "Phase 6"; Phase 3 does groundwork only, no VAPID).

## Consequences

**Positive**
- Zero-cost, no-Docker realtime that satisfies the free/local constraint and the notification UX.
- Dependency rule intact: Application defines the port + DTO; the SignalR adapter sits at the composition root
  in Web; Infrastructure is untouched by realtime.
- Push is off the write's critical path and cannot corrupt persistence; the inbox is always correct even if a
  socket is down.
- Chat-ready foundation (ADR 0010) — a future `ChatHub` reuses hosting, auth, and identity unchanged.

**Negative / accepted**
- **No cross-instance fan-out.** With a second instance, a group send would miss users connected to another
  node. Accepted for the single-instance local profile; the backplane is a one-line add when scale-out is
  real (documented trigger, not built).
- The realtime adapter living in Web (not Infrastructure) is a deliberate, justified placement; a reviewer
  expecting all adapters in Infrastructure should read Decision §3.
- One new npm dependency (`@microsoft/signalr`, MIT) bundled locally.

## Alternatives considered

1. **Azure SignalR Service.** Rejected — paid, cloud, violates ADR 0004 (free/local-only).
2. **Redis/Memurai backplane now.** Rejected for Phase 3 — no second instance exists; premature (YAGNI). The
   `IDistributedCache`/backplane seam remains swappable if scale-out is ever needed.
3. **Short-polling / long-polling a notifications endpoint.** Rejected as the primary path for live toasts
   (higher latency + server load for a socket-sized problem), though SignalR itself negotiates down to
   long-polling as a transport fallback automatically. Plain HTTP still serves the no-JS inbox and the
   unread-count fallback endpoint.
4. **Put the SignalR adapter in Infrastructure.** Rejected — Infrastructure would have to reference the hub
   type in Web (forbidden) or a shared client-contract assembly would have to be introduced (needless). The
   composition-root placement in Web is simpler and dependency-correct.

## Related
- ADR 0004 (free/local-only — no Azure SignalR, no Docker Redis), ADR 0009 (`ICurrentUser` / `NameIdentifier`
  identity shared with `Context.UserIdentifier`), ADR 0010 (chat-ready foundation), ADR 0001 (layering —
  port in Application, adapter at the composition root).
- `docs/architecture/phase-3-social-design.md` §9 (SignalR design), §10 (CSP/anti-forgery contract).
- Skills: `.claude/skills/signalr-realtime/SKILL.md`, `.claude/skills/web-push-notifications/SKILL.md`
  (Phase 6), `.claude/skills/security-hardening/SKILL.md`.

---

_Design-only ADR authored 2026-07-03 (verified: `Program.cs` global fail-closed authZ + Identity cookie with
`NameIdentifier`; `SecurityHeadersMiddleware` `connect-src 'self'`; local esbuild bundling with no CDN; no
SignalR types exist in `src/` yet). No application code written._
