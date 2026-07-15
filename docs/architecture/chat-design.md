# Cinora — In-App Chat (1:1 + Group) Solution Design (Authoritative)

- **Status:** Accepted (design) — brainstormed with the user 2026-07-12
- **Date:** 2026-07-12
- **Owner:** architecture-agent (via the master session)
- **Builds on:** [Friends & Social overhaul](friends-social-overhaul-design.md), [Phase 3 social design](phase-3-social-design.md), [solution structure](solution-structure.md)
- **Supersedes:** [ADR 0010 In-app chat DEFERRED](../adr/0010-in-app-chat-deferred.md) — the user has **opted in** (2026-07-12). ADR 0010 already **pre-scoped** this design; this doc realizes it.
- **Governing ADRs:** [0001 Layering](../adr/0001-clean-architecture-layering.md), [0004 Free/local-only](../adr/0004-free-local-only-infrastructure.md), [0005 Hand-rolled mediator](../adr/0005-hand-rolled-mediator.md), [0009 Resource ownership & current-user seam](../adr/0009-resource-ownership-and-current-user-seam.md), [0011 Self-hosted SignalR](../adr/0011-self-hosted-signalr-realtime.md), [0022 User-blocking model](../adr/0022-user-blocking-model.md)
- **New ADR to record during implementation:** **0024 Chat realtime + participation-as-authorization** (§4/§3).

This document is the single source of truth for **in-app chat**: real-time 1:1 and group messaging with an
emoji picker, typing indicators, and read receipts. It builds **entirely on the existing self-hosted SignalR +
friends + blocking foundation** — the realtime infra, ports, keyset pagination, XSS stance, and premium UI seams
are all reused, so **no CSP change and no infra change** are required.

> **Design-only.** No application code was written and no build/test was run producing this document. Every
> `Verify` command in §15 is an acceptance check the *implementing* agent must run and show output for.

> **User's locked scope decisions (2026-07-12):** **friends-only** messaging · **MVP + typing indicators &
> read receipts** · a **built-in emoji picker**.

> **Governing constraints (unchanged; restated):**
> - **Free / local-only (ADR 0004).** Realtime stays **self-hosted SignalR, single instance, no backplane, no
>   Azure, no Docker** (ADR 0011). Offline delivery reuses the existing self-VAPID Web Push.
> - **Hand-rolled mediator (ADR 0005).** `ISender`/`IRequest<T>`/`IRequestHandler<,>` from
>   `Cinora.Application/Common/Messaging`. **Never add MediatR. Never add FluentAssertions.**
> - **Fail-closed authZ + global anti-forgery + strict CSP** — honored; **no CSP widening** (a `/hubs/chat`
>   WebSocket is same-origin, covered by `connect-src 'self'`; the emoji picker is bundled locally, so
>   `script-src 'self'` is untouched).
> - **No generic repository; no business rules in handlers; no external DTO into Domain; no `Html.Raw` on user
>   content.** All reaffirmed — **chat message bodies are plain text, Razor output-encoded, client `textContent`
>   only** (the existing notification/toast rule, §13).

---

## 1. What chat adds and where every concern lives

Chat introduces **three new entities**, one **realtime hub**, one **Application port**, and a **feature
vertical** — mirroring the Phase-3 realtime + Friends patterns exactly.

| Concern | Layer / location | Notes |
|---|---|---|
| **`Conversation` / `ConversationMember` / `Message` entities** | `Cinora.Domain/Entities/` | Sealed, private ctor, `private set`, static factories, `Guid Id` app-assigned. §2. |
| **EF configs** | `Cinora.Infrastructure/Persistence/Configurations/` | One `IEntityTypeConfiguration<T>` each; keyset index `Message(ConversationId, SentAtUtc)`; unique `ConversationMember(ConversationId, UserId)`; `Restrict` FKs. §11. |
| **DbSets** | `IAppDbContext` + `CinoraDbContext` | `Conversations`, `ConversationMembers`, `Messages`. §11. |
| **Chat verticals** — start-DM, create-group, send, list, history, mark-read, add/remove/leave/rename, delete-message | `Cinora.Application/Features/Chat/` | Request + handler + validator + VM colocated. Deps: `IAppDbContext` + `ICurrentUser` + `IChatNotifier`. §5. |
| **`IChatNotifier` port** + `ChatMessageDto`/`ConversationEventDto` | `Cinora.Application/Common/Interfaces/IChatNotifier.cs`, `Common/Realtime/` | NEW. Push abstraction; DTOs on the wire, never a Domain entity (mirrors `IRealtimeNotifier`). §4. |
| **`ChatHub` + `IChatClient`** | `Cinora.Web/Hubs/ChatHub.cs` | `[Authorize]`, **participants-only `conversation-{id}` groups** (membership-checked on join). Maps `/hubs/chat`. §4. |
| **`SignalRChatNotifier : IChatNotifier`** | `Cinora.Web/Infrastructure/SignalRChatNotifier.cs` | Adapter over `IHubContext<ChatHub, IChatClient>`; registered in `Program.cs` (ADR 0011 §3 — Infrastructure stays out of realtime). §4. |
| **`ChatController`** | `Cinora.Web/Controllers/ChatController.cs` | `[Authorize]`; `ISender` + HTMX partials/redirects. §6. |
| **Chat client TS** (lazy `/hubs/chat` connection, emoji picker, typing, read) | `Cinora.Web/Scripts/` (bundled, no CDN) | Reuses the `site.ts` lazy-import + DOM-marker gate pattern. §4/§7/§8. |
| **Chat unread badge** in the nav | `Cinora.Web/Views/Shared/_Layout.cshtml` + a view component | Mirrors the notification bell; realtime-updated. §9. |
| **DI wiring** — `IChatNotifier`, `MapHub("/hubs/chat")`, a scoped membership-check service for the hub | `Program.cs` | §4/§10. |

**Anti-patterns still banned (reaffirmed):** no generic `IRepository<T>`; no business rules in handlers
(invariants stay on the entities); no `IConfiguration` in services; no Domain/EF type on a realtime/HTMX wire;
**no `Html.Raw` on a message body / display name** (§13).

---

## 2. Data model (record as part of ADR 0024)

Three entities, one **additive** migration `AddChat`.

### `Conversation`
| Field | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK, `ValueGeneratedNever`. |
| `Type` | `ConversationType` `{ Direct, Group }` | Direct = 1:1; Group = many. |
| `Title` | `string?` | Group name (≤ `TitleMaxLength`); `null` for Direct (the UI derives the DM title from the other member). |
| `CreatedByUserId` | `Guid` | The starter (Group creator = the initial Admin). |
| `CreatedAtUtc` / `LastMessageAtUtc` | `DateTime` | `LastMessageAtUtc` is denormalized (bumped on each send) so the conversation list sorts without scanning messages. |

Factories: `Conversation.CreateDirect(a, b)` and `Conversation.CreateGroup(creatorId, title)`. A group title is
guarded (non-blank, length). `BumpLastMessage(atUtc)` updates the sort stamp.

### `ConversationMember`
| Field | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK. |
| `ConversationId` / `UserId` | `Guid` | **Unique `(ConversationId, UserId)`**; index `(UserId)` for "my conversations". |
| `Role` | `ConversationRole` `{ Member, Admin }` | Group creator = Admin; Direct members are both `Member`. |
| `JoinedAtUtc` | `DateTime` | Also the promotion tiebreaker (earliest-joined is promoted if the Admin leaves). |
| `LastReadAtUtc` | `DateTime` | Drives unread counts **and** read receipts. Advanced by `MarkRead`. |

### `Message`
| Field | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK. |
| `ConversationId` | `Guid` | Index `(ConversationId, SentAtUtc)` — the history keyset. |
| `SenderId` | `Guid` | |
| `Body` | `string` | Plain text, `≤ BodyMaxLength` (e.g. 4000). **Never HTML.** |
| `SentAtUtc` | `DateTime` | |
| `IsDeleted` | `bool` | Sender soft-delete → the row stays (so read positions/keyset hold) and renders "This message was deleted". |

Factory `Message.Create(conversationId, senderId, body)` (guards non-blank + length). `MarkDeleted()`.

All FKs to `Users`/`Conversation` are `OnDelete(DeleteBehavior.Restrict)` (the SQL-Server multiple-cascade-paths
rule, consistent with `Friend`/`UserBlock`/`Notification`).

---

## 3. Access rules — friends-only + blocking + participation-as-authorization

Three gates, all **server-side in the handler** (ADR 0009 — the actor is `ICurrentUser`, never a bound field):

1. **Start a DM** (`StartDirectConversationCommand`): the target must be an **accepted friend**
   (`FriendProjections.AcceptedFriendIdsAsync`) **and not blocked either way**
   (`BlockQueries.AreBlockedEitherWayAsync`). **Find-or-create** — one Direct conversation per unordered pair
   (handler guard, like the directed-pair friend guard); a second attempt returns the existing one.
2. **Create / add-to a group** (`CreateGroupConversationCommand` / `AddGroupMembersCommand`): you may add only
   **your own accepted friends** who aren't blocked. **Creator = Admin**; Admin may rename + add/remove; any
   member may **leave**; if the Admin leaves, the **earliest-joined** remaining member is promoted (a group is
   never left admin-less while it has members).
3. **Every message send / history read / mark-read** (`SendMessageCommand`, `GetMessagesQuery`,
   `MarkConversationReadCommand`): the current user **must be a `ConversationMember`** — participation **is** the
   authorization (a non-member → `ForbiddenAccessException` → 403; a missing conversation → 404). A DM send
   **re-checks not-blocked** (block after the DM exists → send rejected).

**MVP simplification (flagged, §14):** a pre-existing shared **group** between two people who later block each
other is **not** torn apart (per-pair message hiding inside a group is out of scope); blocking fully governs
**DMs** and **new group adds**. Acceptable for the local/social profile.

---

## 4. Realtime — `ChatHub` (mirrors `NotificationHub`, ADR 0011; record as ADR 0024)

- **`ChatHub : Hub<IChatClient>`**, `[Authorize]` (fail-closed — an anonymous socket never joins a group).
  Groups are **`conversation-{id}`**, joined **only after a server-side membership check** (ADR 0010 §2). The
  hub injects a small **scoped membership-check** (one `Any()` on `ConversationMembers`) — the single, sanctioned
  reason a hub touches data. Mapped `app.MapHub<ChatHub>("/hubs/chat")` **after** `UseAuthentication`.
- **`IChatClient`** (strongly-typed): `ReceiveMessage(ChatMessageDto)`, `UserTyping(Guid conversationId, Guid
  userId, string displayName)`, `ConversationRead(Guid conversationId, Guid userId, DateTime lastReadAtUtc)`,
  `ConversationUpdated(ConversationEventDto)` (new conversation / membership change → the list refreshes).
- **Hub methods (thin):** `JoinConversation(Guid id)` (membership-checked → `Groups.AddToGroupAsync`),
  `LeaveConversationGroup(Guid id)`, and **`Typing(Guid id)`** → `Clients.OthersInGroup("conversation-{id}")
  .UserTyping(...)` (ephemeral, **no DB write**). Typing is the *only* thing the hub broadcasts directly;
  everything persisted flows through commands.
- **`IChatNotifier` port** (Application) + **`SignalRChatNotifier`** (Web adapter over `IHubContext`): the
  `SendMessageCommand` handler, **after its `SaveChanges`**, calls `IChatNotifier.MessageSentAsync(convId, dto,
  ct)` **best-effort** (try/catch → `LogWarning`; a push failure never fails the persisted send — the message is
  always in the DB). Read-receipt + membership-change events push the same best-effort way. **Offline delivery:**
  the send handler also enqueues **Web Push** (`IPushDispatch`) for members whose `LastReadAtUtc` is stale and
  who opted in — reusing the existing push pipeline (ADR 0020).
- **Client (TS, bundled, no CDN):** a **lazy** `import("@microsoft/signalr")` connection to `/hubs/chat`,
  **gated on a chat-surface DOM marker** (present only on `/chat`), `.withAutomaticReconnect(...)`, handlers for
  `ReceiveMessage`/`UserTyping`/`ConversationRead`/`ConversationUpdated`. Mirrors the `site.ts`
  `connectNotifications` pattern exactly. **`connect-src 'self'` already admits the same-origin `wss://` — no
  CSP change.**

**Send → render (no double-append):** `POST /chat/{id}/messages` → `SendMessageCommand` persists + pushes to the
group; the POST **returns the rendered `_ChatMessage` partial** for the sender's immediate append, and the
realtime `ReceiveMessage` echo is **deduped by `data-message-id`** on every client (append only if that id isn't
already in the DOM). One rendering path, instant feedback, realtime for everyone else.

---

## 5. Application verticals (`Cinora.Application/Features/Chat/`)

| Request | Kind | Deps | Returns | Notes |
|---|---|---|---|---|
| `StartDirectConversationCommand(Guid OtherUserId)` | Command | db, currentUser | `Guid ConversationId` | Friend ∧ ¬blocked; **find-or-create** one Direct per pair. |
| `CreateGroupConversationCommand(string Title, IReadOnlyList<Guid> MemberIds)` | Command (+validator) | db, currentUser | `Guid ConversationId` | Members must be the creator's friends ∧ ¬blocked; creator = Admin. |
| `SendMessageCommand(Guid ConversationId, string Body)` | Command (+validator) | db, currentUser, chatNotifier | `ChatMessageVm` | Membership check; DM re-check ¬blocked; `Message.Create` → bump `LastMessageAtUtc` → push (§4) + Web-Push offline. |
| `GetConversationsQuery()` | Query | db, currentUser | `ConversationsVm` | My conversations (member), newest-first by `LastMessageAtUtc`, each with the other member/title, last-message preview, and **unread count** (`Messages.Count(m => m.SentAtUtc > myLastReadAtUtc && m.SenderId != me)`), N+1-free. |
| `GetMessagesQuery(Guid ConversationId, MessageCursor?, int Take=30)` | Query | db, currentUser | `MessageThreadVm` | Membership check; keyset `(SentAtUtc DESC, Id DESC)`, `Take+1`. |
| `MarkConversationReadCommand(Guid ConversationId)` | Command | db, currentUser, chatNotifier | `Unit` | Advance my `LastReadAtUtc`; push `ConversationRead` (read receipts + unread badge). |
| `AddGroupMembersCommand(Guid ConversationId, IReadOnlyList<Guid> MemberIds)` | Command | db, currentUser, chatNotifier | `Unit` | Member adds own friends (¬blocked); push `ConversationUpdated`. |
| `RemoveGroupMemberCommand(Guid ConversationId, Guid MemberId)` | Command | db, currentUser, chatNotifier | `Unit` | Admin-only. |
| `LeaveConversationCommand(Guid ConversationId)` | Command | db, currentUser, chatNotifier | `Unit` | Remove my membership; promote earliest-joined if I was the Admin. |
| `RenameGroupCommand(Guid ConversationId, string Title)` | Command (+validator) | db, currentUser, chatNotifier | `Unit` | Admin-only. |
| `DeleteMessageCommand(Guid MessageId)` | Command | db, currentUser, chatNotifier | `Unit` | **Sender-only** (`ForbiddenAccessException` otherwise) → `MarkDeleted()` (soft); push a delete event. |

Reads are `AsNoTracking().Select(...)` projections; writes load a tracked entity → domain method →
`SaveChanges` → best-effort push. VMs/DTOs are Cinora primitives — **no Domain/EF type crosses a wire**.

---

## 6. UI (premium, `/chat`)

(frontend-agent + ux-agent; skills: `premium-ui-design`, `razor-views`, `alpine-htmx-interactivity`,
`signalr-realtime`, `ui-animations`, `responsive-accessibility`.)

- **`/chat` two-pane layout** (desktop): a **conversation list** (left) — each row `_Avatar` + name/title +
  last-message preview + timestamp + **unread badge**, sorted by recency; and the **active thread** (right) —
  a scrollable message list of `_ChatMessage` bubbles (yours accent/right, theirs surface/left, avatars +
  timestamps, "message deleted" tombstone), a **composer** (textarea + the **😊 emoji-picker button** + Send),
  a live **"typing…"** row, and **read receipts** ("Seen" on DMs; "Seen by N" on groups). On **mobile** it is a
  stacked flow: list → tap → thread (with a back affordance).
- **New chat:** a **New group** modal (name + pick from your friends) and a **"Message"** action on a friend's
  profile / friends list that opens (find-or-creates) the DM.
- **Group management:** Admin controls (rename, add members from friends, remove) + **Leave** — destructive
  actions behind the existing **CSP-safe styled confirm** (`data-confirm-*`).
- **History:** load-more via the keyset cursor (reuse the sentinel grammar); the thread auto-scrolls to newest;
  new realtime messages append and (if you're at the bottom) keep you pinned.
- **Nav:** a **chat unread badge** (a view component beside the notification bell), realtime-updated.
- **Encoding/a11y:** every message `Body` and `DisplayName` is **Razor `@`-encoded**; client appends set
  **`textContent`, never `innerHTML`** (§13). The thread is an `aria-live` region for new messages; the emoji
  picker + composer are keyboard-accessible; reduced-motion respected. Dark-luxury tokens + `_Avatar`,
  `card-glass`, `btn`, `rounded-*` reused throughout.

**CSP/contract extensions: none** (§10) — deliberate.

---

## 7. Emoji picker (built-in, CSP-safe)

A **locally-bundled** emoji picker — a `😊` button in the composer opens an Alpine popover with a **curated,
categorized grid** of common emojis (a bundled emoji-data list, no external service/CDN). Selecting one inserts
it at the caret in the message textarea. Because it's a bundled `@alpinejs/csp` bare-name component (no inline
handlers, no `eval`), **`script-src 'self'` is untouched**. The device's **native** emoji keyboard also works
(the textarea is plain). YAGNI: no custom-emoji upload, no skin-tone matrix in MVP (a flat common set) — a
scoped follow-up if wanted.

---

## 8. Typing indicators & read receipts (the "+MVP" tier)

- **Typing:** as you type, the client calls **`hub.Typing(conversationId)`** debounced (~1.5–2 s, and on stop);
  the hub broadcasts `UserTyping` to **`OthersInGroup`**; recipients show "*X is typing…*" that auto-clears
  after a short timeout. **Ephemeral — never persisted, no DB, no notification.**
- **Read receipts:** when a conversation is open/focused (and on each new message while focused), the client
  **`POST /chat/{id}/read`** (debounced) → `MarkConversationReadCommand` advances my `LastReadAtUtc` and pushes
  `ConversationRead(convId, me, readAt)`. Senders mark their messages `≤ readAt` as **"Seen"** (DM) or update a
  **"Seen by N"** count (group). The same `LastReadAtUtc` powers the **unread counts** (§5/§9), so receipts and
  unread badges stay consistent from one source of truth.

---

## 9. Notifications, unread badge, offline push

- A **global chat unread badge** in the nav (a view component, seeded server-side from `GetConversationsQuery`'s
  unread sum, updated live over `/hubs/chat`), mirroring the notification bell.
- Per-conversation unread badges in the list.
- **Offline / not-in-chat:** the `SendMessageCommand` enqueues **Web Push** (`IPushDispatch`, ADR 0020) to
  members with stale `LastReadAtUtc` who opted in — reusing the existing VAPID pipeline and notification
  preferences. *(In-app toast for a new message while elsewhere in the app is a small optional add — the badge +
  realtime cover MVP.)*
- **No new `NotificationType`** is required for MVP (chat has its own unread channel); a "new message"
  `Notification` row is an optional later enhancement.

---

## 10. Anti-forgery / authZ / CSP / rate-limit contract

1. **Fail-closed authZ.** `ChatController` is `[Authorize]`; `ChatHub` is `[Authorize]` with membership-checked
   group joins. No `[AllowAnonymous]`.
2. **Anti-forgery.** Every state-changing POST/DELETE (send, create, add/remove/leave/rename, read, delete)
   carries the token via the wired `RequestVerificationToken` header; the global `AutoValidateAntiforgeryToken`
   validates it. No opt-out.
3. **CSP — NO widening.** `/hubs/chat` is same-origin (`connect-src 'self'` covers `wss://<host>/hubs/chat`);
   the SignalR client + emoji picker are locally bundled (`script-src 'self'` untouched); avatars are `img-src`
   'self'. **CSP SHA-256 stays byte-unchanged.**
4. **Rate limiting.** `SendMessageCommand`'s POST is under the existing **`social-write`** per-user policy (blunt
   spam). Typing (hub) is naturally debounced client-side; the hub trusts the membership-gated group.

---

## 11. Migration / schema (for database-agent)

**One new migration `AddChat`, additive:**

| Table | Key columns | Indexes |
|---|---|---|
| `Conversations` | `Id` PK, `Type`, `Title?`, `CreatedByUserId`, `CreatedAtUtc`, `LastMessageAtUtc` | FK `CreatedByUserId → Users` (Restrict); index `(LastMessageAtUtc)` optional. |
| `ConversationMembers` | `Id` PK, `ConversationId`, `UserId`, `Role`, `JoinedAtUtc`, `LastReadAtUtc` | **unique `(ConversationId, UserId)`**, index `(UserId)`; FKs → `Conversations` + `Users` (**Restrict**). |
| `Messages` | `Id` PK, `ConversationId`, `SenderId`, `Body`, `SentAtUtc`, `IsDeleted` | **`(ConversationId, SentAtUtc)`** keyset; FKs → `Conversations` + `Users` (**Restrict**). |

Generated in `Cinora.Infrastructure`, startup `Cinora.Web`; applied via the reviewed idempotent SQL script /
`db-update.ps1` (the app never auto-migrates). Apply to `CinoraTest` (auto) + the remote `db58983` at deploy.

---

## 12. Performance stance

- **Keyset pagination** for history (`(SentAtUtc, Id)` cursor, `Take+1`, never `OFFSET`) backed by the
  `(ConversationId, SentAtUtc)` index.
- **`AsNoTracking().Select(...)`** projections; the conversation-list **unread count + last-message preview are
  correlated subqueries in one query** (no N+1). `LastMessageAtUtc` denormalization avoids scanning messages to
  sort the list.
- **Thin hub:** the hub does one membership `Any()` on join and pure broadcasts otherwise; all heavy/persisted
  work is in commands, off the socket. Realtime push is **after** `SaveChanges`, best-effort.
- **Async end-to-end**, `CancellationToken` threaded controller → `ISender` → handler → EF/`IChatNotifier`.
- Single instance, no backplane (ADR 0011) — a documented scale trigger, not an MVP concern.

---

## 13. Security stance

- **XSS:** message `Body` + `DisplayName` are user-controlled → **Razor output-encoding only**, **no
  `Html.Raw`**, and every client append sets **`textContent`** (never `innerHTML`) — the exact existing
  notification/toast rule. `ChatMessageDto.Body` is documented "rendered PLAIN TEXT".
- **Participation-as-authorization:** membership is re-checked **server-side** on every send/read/history and on
  every hub group-join — a non-member can neither read nor push into a conversation (→ 403). The actor is always
  `ICurrentUser`, never a bound field.
- **Blocking fail-closed:** DMs consult `BlockQueries.AreBlockedEitherWayAsync` on start **and** send; blocked
  users can't message or be added.
- **Anti-forgery + `social-write` rate limit** on the send/mutation POSTs (§10).
- **No PII on the wire beyond what chat needs** (display name + avatar + message text); DTOs carry no email/id
  leakage beyond the conversation's own members.

---

## 14. Risks and product decisions

**Risks baked in:**
- **Best-effort realtime:** a dropped socket means a missed live message *view*, never a lost message
  (persistence is the source of truth; opening the thread reloads from the DB). Single instance = no cross-node
  push (documented scale trigger).
- **Hub membership check on join** is one DB read per conversation-open — cheap, indexed, acceptable; it's the
  one sanctioned hub data-touch (ADR 0010 §2).
- **Send/echo dedupe** relies on `data-message-id`; a client bug could double-render — covered by a test that
  the POST-append + realtime-echo yield one node.

**Product decisions (locked with the user 2026-07-12 unless noted):**
- **Friends-only** messaging (DMs + group adds). ✅ locked.
- **MVP + typing indicators & read receipts.** ✅ locked. **Deferred (YAGNI):** message reactions, threaded
  replies, message editing, media/file attachments, custom emoji.
- **Built-in emoji picker** (curated, local). ✅ locked.
- **Group admin model:** creator = Admin; Admin renames + adds/removes; anyone leaves; Admin-leave promotes the
  earliest-joined member. Default — revertible.
- **Read receipts in groups:** a lightweight **"Seen by N"** (not a full per-member seen-list). Default.
- **Blocking vs pre-existing shared groups:** not torn apart (§3). Flagged default.

---

## 15. Milestone build order (delegable)

Ordered for the orchestrator; each states owning agent(s), deliverable, exact `Verify`, and the review gates
(`/review-architecture`, `/review-code`, `/review-security`, `/review-performance`, `/review-ui`) that run after
every milestone and at the end (per `CLAUDE.md`). Integration tests run against real LocalDB `CinoraTest`.

### C1 — Data model + persistence + migration
- **Owner:** database-agent + architecture-agent (ADR 0024).
- **Deliverable:** `Conversation`/`ConversationMember`/`Message` entities (+ enums, factories, guards) + domain
  tests; the three `IEntityTypeConfiguration`s; `IAppDbContext`/`CinoraDbContext` DbSets; the **`AddChat`**
  additive migration (+ regenerate `artifacts/migrate.sql`); a persistence round-trip test.
- **Verify:** `dotnet build Cinora.sln -c Release` clean; `dotnet test tests/Cinora.Domain.Tests/...` +
  `tests/Cinora.Web.IntegrationTests/... --filter Chat` green; migration is additive-only.

### C2 — Access + send/history/list verticals (no realtime yet)
- **Owner:** backend-agent + security-agent (friends/block/membership gates).
- **Deliverable:** `StartDirectConversationCommand` (find-or-create + friend/block gate),
  `CreateGroupConversationCommand`, `SendMessageCommand` (membership + DM-block gate, persist + bump; push
  stubbed/no-op until C3), `GetConversationsQuery` (unread + preview, N+1-free), `GetMessagesQuery` (keyset),
  `MarkConversationReadCommand`, add/remove/leave/rename, `DeleteMessageCommand`. Handler-level tests
  (direct-handler construction + NSubstitute `ICurrentUser`, users seeded via `TestAuthentication`, per the
  Friends-overhaul pattern): DM find-or-create, non-friend/blocked can't DM, non-member send → 403, keyset
  history no-overlap, unread count, leave-promotes-admin, sender-only delete.
- **Verify:** `dotnet test tests/Cinora.Application.Tests/...` + `.../Web.IntegrationTests --filter Chat` green.

### C3 — `ChatHub` + realtime push (`IChatNotifier`)
- **Owner:** backend-agent + security-agent (hub auth + membership join) + architecture-agent (ADR 0024).
- **Deliverable:** `IChatNotifier` + DTOs; `ChatHub` + `IChatClient` (`[Authorize]`, membership-checked
  `conversation-{id}` join, `Typing` broadcast); `SignalRChatNotifier` registered in `Program.cs`;
  `MapHub("/hubs/chat")`; wire `SendMessageCommand`/`MarkConversationReadCommand`/membership commands to push
  best-effort. Tests: **hub requires auth**, **non-member can't join a conversation group**, **push failure
  doesn't fail the send**, **CSP unchanged** (`connect-src 'self'`).
- **Verify:** integration tests green; `npm run build --prefix src/Cinora.Web` bundles the (existing) SignalR
  client; CSP SHA-256 unchanged.

### C4 — Premium `/chat` UI + emoji picker
- **Owner:** frontend-agent + ux-agent.
- **Deliverable:** `ChatController` + `/chat` two-pane page + `_ConversationRow`/`_ChatMessage`/`_MessageThread`
  partials + New-group modal + the "Message" affordance on profiles/friends; the **lazy `/hubs/chat` client** in
  `site.ts` (gated on a chat DOM marker) with `ReceiveMessage` append + **`data-message-id` dedupe**; the
  **bundled emoji picker**; the chat unread-badge view component in the nav. All dark-luxury, `textContent`-safe,
  a11y.
- **Verify:** `dotnet test .../Web.IntegrationTests --filter Chat` (page renders, send returns `_ChatMessage`,
  history load-more, blocked-can't-DM through HTTP) green; `npm run build` clean; **manual two-browser smoke**
  (A sends → B sees it live). Then `/review-ui` + `/review-security` + `/review-code`.

### C5 — Typing indicators + read receipts
- **Owner:** backend-agent + frontend-agent.
- **Deliverable:** the `hub.Typing` broadcast + client "typing…" UI (debounced); the read flow
  (`POST /chat/{id}/read` → `MarkConversationReadCommand` → `ConversationRead` push) + "Seen"/"Seen by N" UI;
  unread badges recompute from `LastReadAtUtc`. Tests: mark-read advances `LastReadAtUtc` + drops unread;
  read-receipt push shape; typing is ephemeral (no DB).
- **Verify:** integration tests green; manual smoke (typing + Seen across two browsers).

### C6 — Offline Web Push + polish
- **Owner:** backend-agent (push) + pwa-agent.
- **Deliverable:** enqueue Web Push on send for stale/opted-in members (reuse `IPushDispatch` + prefs); the SW
  push handler already renders a notification (ADR 0020) — add a chat deep-link (`/chat?c={id}`). Tests:
  push enqueued for an offline member, suppressed for a muted/opted-out member.
- **Verify:** integration tests green; **manual** real-push round-trip (carried).

**End-of-feature gate:** build clean (TWAE on); `dotnet test` green across all four projects; 1:1 + group
chat with emoji, typing, read receipts, unread badges, and offline push all functioning; **one additive
migration** (`AddChat`); **CSP byte-unchanged**; **zero Critical/High** across the five review gates; ADR 0024
recorded. Then deploy (app + `AddChat` applied to `db58983`) per the agreed deploy mode.

---

_Design authored 2026-07-12 against the live post-Phase-6 + Friends-overhaul code (verified: `NotificationHub`
`[Authorize]` group-per-user + `INotificationClient`; `IRealtimeNotifier`/`SignalRRealtimeNotifier`/
`RealtimeNotificationDispatcher` best-effort; the `@microsoft/signalr` lazy-import client in `site.ts` gated on a
DOM marker; `connect-src 'self'`; `FriendProjections.AcceptedFriendIdsAsync`; `BlockQueries.AreBlockedEitherWayAsync`;
`ICurrentUser`; the `Notification` entity/config/keyset conventions; the `_Avatar`/`card-glass`/styled-confirm UI
seams; and that **no** `Conversation`/`Message`/`ChatHub` code exists — ADR 0010 pre-scoped it). No application
code written._
