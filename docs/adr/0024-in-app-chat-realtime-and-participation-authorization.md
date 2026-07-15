# ADR 0024 — In-App Chat: Self-Hosted SignalR Realtime + Participation-as-Authorization

- **Status:** Accepted (supersedes ADR 0010 "in-app chat — deferred")
- **Date:** 2026-07-14
- **Phase:** Post-Phase-6 — In-App Chat (Milestones C1–C5)
- **Deciders:** architecture-agent (design), database-agent (entities/config/migration), backend-agent
  (verticals + hub + adapter), frontend-agent (premium `/chat` UI + emoji picker + realtime client),
  security-agent (fail-closed participation gate + XSS/CSP), orchestrator (ratify)

## Context

The PRD lists **in-app chat**; ADR 0010 deferred it (absent from the schema and `Prompt.txt`'s feature list).
The user has now opted in and set the scope: **friends-only 1:1 and group messaging, an MVP tier plus typing
indicators and read receipts, with a built-in emoji picker.** Chat must reuse — not duplicate — the platform's
existing seams: the self-hosted SignalR realtime pattern (ADR 0011), the friends/blocking graph and its
fail-closed `BlockQueries` seam (ADR 0022), the current-user + resource-ownership rule (ADR 0009), the
`_Avatar` render seam (ADR 0013), and the strict CSP / anti-forgery / rate-limit posture. Two questions had to
be settled: **how conversations authorize access**, and **how realtime is delivered without widening the CSP or
leaking domain/vendor types onto the wire.**

## Decision

**Model chat as three additive Domain entities behind CQRS verticals, authorize every operation by
participation (membership) enforced server-side in the handler, and deliver realtime over a self-hosted
`ChatHub` behind an Application `IChatNotifier` port — mirroring the notification hub, with zero CSP change.**

### 1. Data model (three additive entities, one migration)

`Conversation(Id, Type{Direct,Group}, Title?, CreatedByUserId, CreatedAtUtc, LastMessageAtUtc)`,
`ConversationMember(Id, ConversationId, UserId, Role{Member,Admin}, JoinedAtUtc, LastReadAtUtc)`, and
`Message(Id, ConversationId, SenderId, Body, SentAtUtc, IsDeleted)`. `LastMessageAtUtc` is denormalized (bumped
on each send) so the conversation list sorts by recency without scanning messages; `LastReadAtUtc` drives both
unread counts and read receipts; delete is a **soft tombstone** so the keyset history and read positions stay
stable. One **additive** migration (`AddChat`): three tables, a unique `(ConversationId, UserId)` membership
index, a `(ConversationId, SentAtUtc)` history keyset index, and `Restrict` FKs to `Users` (avoiding the SQL
Server multiple-cascade-paths failure, as `Friend`/`UserBlock` already do).

### 2. Participation-as-authorization (fail-closed, in the handler)

There is no per-row ACL: **membership is the authorization.** Every read/write handler resolves the actor
server-side (`ICurrentUser`, ADR 0009 — never a bound field) and checks the `ConversationMembers` set itself:
a non-member throws `ForbiddenAccessException` (→ 403), a missing conversation `NotFoundException` (→ 404). DMs
additionally re-check the block gate at send time (a friendship can change after a conversation exists); group
membership changes are friends-only (`FriendProjections` + `BlockQueries`); rename/remove are admin-only;
delete is sender-only. The `ChatHub` never joins a connection to a `conversation-{id}` group without the same
membership check, so a non-participant can never receive a conversation's messages even over the socket.

### 3. Realtime — a self-hosted `ChatHub` behind an `IChatNotifier` port

`IChatNotifier` (Application) carries only Cinora-owned wire records (`ChatMessageDto`/`ChatReadDto`/
`ConversationEventDto`) — **no Domain entity or EF/vendor type crosses the wire.** The Web adapter
`SignalRChatNotifier` wraps `IHubContext<ChatHub, IChatClient>` and pushes **best-effort** (try/catch → Warning;
a push failure never rolls back or fails the persisted write — the rows are authoritative, mirroring
ADR 0011). Messages/read receipts fan out to the `conversation-{id}` group; membership changes to each member's
per-user `chat-user-{id}` group. The hub is `[Authorize]` fail-closed (an anonymous negotiate is rejected; a
connection with no stable `NameIdentifier` is aborted, never degraded into a shared group).

### 4. Premium UI, XSS, CSP, anti-forgery — no infra change

A two-pane `/chat` surface (conversation list + thread) in the dark-luxury system, the shared `_Avatar` seam, a
composer with a **locally-bundled** emoji picker (a curated categorized set — no external service), typing
indicators, and "Seen" read receipts. All message text is **PLAIN TEXT**: Razor `@`-encoded on render, and the
realtime client sets it via `textContent`, never `innerHTML` (no `Html.Raw`). The hub is same-origin
(`wss://…/hubs/chat`, already admitted by `connect-src 'self'`), the emoji picker and chat client are bundled
locally under `/dist` (`script-src 'self'`), and all interactivity is `addEventListener`-wired (no inline JS,
no eval) — so the **strict CSP is byte-unchanged** (the SHA-256-pinned policy since Phase 2). Every
state-changing POST is anti-forgery-validated (form tag helper hidden field or the `RequestVerificationToken`
header) and rate-limited with the shared per-user `SocialWrite` policy.

## Consequences

**Positive**
- Chat reuses every existing seam (SignalR, friends/blocking, current-user, `_Avatar`, CSP/anti-forgery/rate
  limit) rather than duplicating them; the security-sensitive rules inherit their proven implementations.
- Participation-as-authorization is one rule applied uniformly in the handlers and the hub — no per-row ACL to
  drift, no controller-bound actor to spoof.
- Additive-only migration; zero risk to existing data. Serving the thread makes no vendor calls; realtime is a
  best-effort accelerator over authoritative rows.
- The `IChatNotifier` boundary keeps the transport swappable and the wire free of Domain/vendor types.

**Negative / accepted**
- **Offline Web Push on a new message (the original C6) is deferred** — out of the chosen MVP+ scope. The
  existing `IPushDispatch` pipeline is keyed to a `Notification` inbox row per push; reusing it per chat message
  would pollute the notification inbox, and a parallel chat-push path exceeds the chosen scope. The
  `IChatNotifier`/`ChatHub` seam leaves room to add it later without rework (a future `chat-user-{id}` fan-out
  to stale, opted-in members).
- Live conversation-list bumps / nav-badge increments for a user viewing a *different* thread are not pushed;
  the nav badge is server-seeded and accurate on navigation. Accepted for the chosen scope.
- Group "Seen by N" is simplified to a single "Seen" receipt (DM-oriented); a per-member seen roster is a
  future enhancement.
- The two-browser realtime smoke (message + typing + Seen) is a carried manual verification, consistent with
  the project's other live-transport smokes.

## Alternatives considered

1. **Per-row ACL / explicit permission table.** Rejected — membership already models exactly who may participate;
   a second ACL is redundant and drift-prone.
2. **Azure SignalR / a Redis backplane.** Rejected — violates the free/local-only constraint; the single-instance
   self-hosted hub (ADR 0011) suffices.
3. **A `Notification` row per chat message to reuse the push + badge pipeline.** Rejected — floods the inbox and
   conflates two concerns; chat keeps its own unread model (`LastReadAtUtc`).
4. **Vendor/EF types on the SignalR wire.** Rejected — leaks the model and couples the client to persistence;
   Cinora-owned DTOs instead.
5. **Widen the CSP (a CDN emoji picker / a cross-origin socket host).** Rejected — the emoji set is bundled
   locally and the hub is same-origin, so the strict CSP is untouched.

## Related

- Design: `docs/architecture/chat-design.md`; plan `docs/superpowers/plans/2026-07-12-chat.md`; ledger
  `docs/superpowers/plans/2026-07-12-chat-progress.md`.
- Supersedes ADR 0010 (in-app-chat-deferred). Builds on ADR 0011 (self-hosted SignalR realtime), ADR 0009
  (current-user + resource-ownership), ADR 0022 (blocking model + `BlockQueries` seam), ADR 0012
  (`FriendProjections`), ADR 0013 (`_Avatar` seam), ADR 0023 (authenticated-only access).
- Skills: `signalr-realtime`, `premium-ui-design`, `alpine-htmx-interactivity`, `security-hardening`,
  `ef-core-data-access`, `ef-core-migrations`.

---

_Authored 2026-07-14 against the shipped Milestone C1–C5 code (verified: 3 entities + configs + `AddChat`
additive migration; the CQRS verticals with participation/friends/blocking/admin/sender gates; `ChatHub` +
`SignalRChatNotifier` + `IChatMembership` wired at `/hubs/chat`; the two-pane `/chat` UI + bundled emoji picker
+ typing + read receipts; CSP byte-unchanged; 540 tests green). No git performed; the human commits._
