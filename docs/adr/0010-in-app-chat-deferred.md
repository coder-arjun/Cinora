# ADR 0010 — In-App Chat Deferred out of Phase 3

- **Status:** Accepted (pending user ratification of scope)
- **Date:** 2026-07-03
- **Phase:** 3 (Reviews & Social) — scope decision
- **Deciders:** architecture-agent (recommendation), orchestrator + user (product scope — flagged)

## Context

The **PRD** lists "in-app chat between users" as a core feature. It is, however, **absent from every other
source of truth**:

- **Backend_Schema.docx** has no chat/message/conversation entity — the twelve entities are
  `User, Friend, Movie, Genre, MovieGenre, Review, ReviewLike, Comment, Watchlist, Notification, Device,
  AIRecommendationHistory`. There is no `Message` or `Conversation`.
- **Implementation_Plan.docx** phases (1 Foundation, 2 Discovery, 3 Reviews, 4 Watchlists, 5 AI, 6 Polish)
  contain no chat milestone.
- **Prompt.txt**'s feature list does not mention chat.

`CLAUDE.md` records this as a "Known spec inconsistency," and `PROGRESS.md` flags it as an **Open Decision
needed before Phase 3**. Phase 3's mandate is Reviews CRUD, Likes+Comments, Friends+profiles, the activity
feed, and Notifications+realtime — a coherent social layer that does **not** require chat.

Building chat now would mean inventing entities, migrations, a hub, and a whole conversation UX that no
approved spec describes, on a guess about scope (1:1 only? group? media sharing? history retention?
moderation?). That is precisely the "manufacture architecture ahead of a decision" YAGNI trap.

## Decision

**Design and build Phase 3 WITHOUT in-app chat. Defer chat to a future, separately-scoped milestone, and
flag the in/out decision to the user as a product call.**

- No `Message`/`Conversation` entity, no `ChatHub`, no chat UI, no chat migration is introduced in Phase 3.
- The Phase-3 realtime investment (self-hosted SignalR, `NotificationHub`, group-per-user, `ICurrentUser`,
  the fail-closed-hub-auth pattern — ADR 0011) is deliberately **chat-ready**: a future `ChatHub` reuses the
  same hosting model, auth, and identity seam with zero rework to the notification path.

## What a future chat milestone would need (scoping note, not a commitment)

1. **Domain + schema:** a `Conversation` (participants; 1:1 vs group is the first product decision) and a
   `Message` (conversation id, sender `UserId`, plain-text body, `SentAtUtc`, read state) entity, with EF
   configurations, keyset indexes (`(ConversationId, SentAtUtc)`), and a migration. Users referenced by
   `Guid` (ADR 0003).
2. **Realtime transport:** a `ChatHub : Hub<IChatClient>`, `[Authorize]` (fail-closed), **participants-only**
   group membership (`conversation-{id}`) with server-side membership checks on join and on every send —
   self-hosted SignalR, **no Azure SignalR, no backplane** unless multi-instance (ADR 0004 / 0011).
3. **CQRS verticals:** `StartConversationCommand`, `SendMessageCommand` (server-resolved sender via
   `ICurrentUser`), `GetConversationsQuery`, `GetMessagesQuery` (keyset), `MarkConversationReadCommand`.
4. **Authorization & safety:** resource ownership = conversation participation, enforced in the handler
   (ADR 0009); **plain-text messages, Razor-encoded, no `Html.Raw`** (the Phase-3 XSS stance); rate-limit the
   send endpoint; anti-forgery N/A on the socket but hub auth is mandatory.
5. **Offline delivery:** pair with Web Push (Phase 6 `Device` groundwork) so messages reach users with the
   tab closed.
6. **Product scope to settle first:** 1:1 vs group; media/attachment sharing (needs `IFileStorage`, size/
   type limits); history retention & deletion (GDPR); typing indicators/read receipts; blocking/reporting.

## Consequences

**Positive**
- Phase 3 ships the approved social layer without speculative, unspecified surface area.
- The realtime foundation is built once and is chat-ready, so a later chat milestone is additive.
- The spec inconsistency is recorded and surfaced for an explicit product decision rather than resolved by a
  silent guess.

**Negative / accepted**
- The PRD's chat feature is not delivered in Phase 3. If the user rules chat **in**, it becomes a scoped
  milestone (recommended after Phase 4, or as an explicit Phase-3 extension) — not a silent addition here.

## Alternatives considered

1. **Build a minimal 1:1 chat in Phase 3.** Rejected: no schema, no plan entry, no agreed scope; it would
   invent product decisions (group vs 1:1, attachments, retention) that belong to the user, and inflate the
   Phase-3 gate with untested surface.
2. **Repurpose `Comment`/`Notification` as a chat substitute.** Rejected: wrong aggregates and wrong
   invariants; would corrupt the review-comment and notification models.

## Related
- ADR 0011 (self-hosted SignalR — the realtime foundation a future `ChatHub` reuses), ADR 0009 (current-user
  seam + participation-as-ownership), ADR 0004 (free/local-only — no Azure SignalR).
- `docs/architecture/phase-3-social-design.md` §14 (chat-deferral section + user flag).
- `CLAUDE.md` ("Known spec inconsistency"), `PROGRESS.md` Open Decisions ("In-app chat").

---

_Design-only ADR authored 2026-07-03. Chat is recommended OUT of Phase 3; the in/out decision is flagged to
the user. No application code or schema written._
