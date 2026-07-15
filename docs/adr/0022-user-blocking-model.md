# ADR 0022 — User-Blocking Model: a Separate `UserBlock` Entity with Full Cut-off + Hide

- **Status:** Accepted
- **Date:** 2026-07-11
- **Phase:** Post-Phase-6 — Friends & Social overhaul (Milestone B1)
- **Deciders:** architecture-agent (design), database-agent (entity/config/migration), backend-agent
  (verticals + enforcement), security-agent (fail-closed hiding), orchestrator (ratify)

## Context

The Friends & Social overhaul (design: `docs/architecture/friends-social-overhaul-design.md`) requires user
**blocking**, with the user-stated semantic *"blocked friends should not see my username in the app."* The
existing social graph is the directed `Friend` entity with `FriendStatus { Pending, Accepted, Declined }` and
the invariant *"only a pending row can transition."* Two questions had to be settled:

1. **Where does a block live** — a new `Blocked` value on `FriendStatus`, or a separate entity?
2. **What does a block enforce**, and how is that enforced consistently across every surface (search, profile,
   friend-request, feed, and future chat)?

## Decision

**Model a block as a new, separate, directional `UserBlock` entity — NOT a `FriendStatus.Blocked` value —
with full cut-off + mutual-hide + one-sided-control semantics, silent to the blocked user, enforced fail-closed
through a single `BlockQueries` seam.**

### 1. A separate `UserBlock` entity

`UserBlock(Id, BlockerId, BlockedUserId, CreatedAtUtc)`; factory `UserBlock.Create(blocker, blocked)` throws
`DomainException` on self-block. **Existence of the row IS the block; unblock deletes the row** — there is no
status/lifecycle. EF config mirrors `FriendConfiguration`: unique index `(BlockerId, BlockedUserId)`, index
`(BlockedUserId)` for the reverse lookup + bulk exclusion, and two `Restrict` FKs to `Users` (avoiding the SQL
Server multiple-cascade-paths failure). One **additive** migration (`AddUserBlock`).

Rejected `FriendStatus.Blocked` because: (a) you can block someone you were never friends with — there is no
`Friend` row to transition; (b) a block is independent of the friendship lifecycle and would break the
"only-pending-can-transition" invariant; (c) a block is a distinct, asymmetric relationship the directed
request pair does not model.

### 2. What a block enforces (full cut-off + hide)

When **A blocks B**, in one unit of work the block is created **and every `Friend` row for the unordered pair
(any status, either direction) is removed**. Thereafter:

- **Mutual hide:** neither sees the other in **user search** (both excluded), in the **feed**, or in social
  lists. B's `GET /users/{A}` → **404** (indistinguishable from a non-existent user — no leak). A's
  `GET /users/{B}` → a minimal **`BlockedByMe`** view (name only + an Unblock control).
- **No contact:** B cannot send A a friend request (and vice-versa) — `SendFriendRequestCommand` returns a
  `Blocked` outcome mapped to a message **identical to a normal send**, so a blocked sender cannot detect the
  block. Chat (future) consults the same guard.
- **One-sided control:** only A can unblock; only A sees B (in the Blocked tab). Unblock does **not** restore
  friendship — the other party must send a fresh request.
- **Silent:** the blocked user is never notified of a block or an unblock.

### 3. One fail-closed enforcement seam

All enforcement routes through `Cinora.Application/Features/Blocks/BlockQueries` — `AreBlockedEitherWayAsync`
(a point check) and `BlockedOrBlockedByIdsAsync` (a bulk exclusion set, N+1-free) — consumed by
`SendFriendRequestCommand`, `GetProfileQuery`, `SearchUsersQuery`, the feed, and (later) chat. Centralizing the
rule prevents an inconsistent application on a new surface. Both checks are cheap seeks on the indexed columns.

## Consequences

**Positive**
- Blocking is independent of the friendship lifecycle; friend queries and invariants are untouched.
- The user's requirement (*"blocked can't see my username"*) is satisfied precisely — mutual hide + 404 + a
  non-revealing friend-request outcome + a non-revealing empty search result.
- One seam = one place to get the security-sensitive hiding rule right; every surface inherits it.
- Additive-only migration; zero risk to existing data.

**Negative / accepted**
- A block and a friend-accept could race; the unique index prevents duplicate blocks, the block's `SaveChanges`
  removes friendship last-writer-wins, and defensive feed exclusion covers a rare interleave — accepted for the
  local/single-instance profile.
- Exact-match search by email still confirms an address exists for a *correct* full address (ADR-adjacent, see
  the search design §14) — a residual accepted under auth-required + rate-limit.
- Two rows can represent a mutual block (A→B and B→A) — accepted; each is one party's independent choice, and
  the guard treats either as sufficient.

## Alternatives considered

1. **Add `FriendStatus.Blocked` to the existing `Friend` entity.** Rejected — can't block a non-friend, breaks
   the transition invariant, conflates two different relationships (§1).
2. **Block = mute only (they keep seeing my public content).** Rejected — does not satisfy *"blocked can't see
   my username."* Chosen full cut-off + hide instead.
3. **Notify the blocked user / show "you were blocked."** Rejected — hostile UX and an information leak; blocking
   is silent, and a blocked friend-request looks like a normal send.
4. **Enforce blocking ad hoc in each handler.** Rejected — inconsistent-application risk on a security-sensitive
   rule; centralized `BlockQueries` seam instead.

## Related

- Design: `docs/architecture/friends-social-overhaul-design.md` §2 (model), §3 (guard), §7 (profile), §14
  (no-leak search).
- ADR 0009 (resource ownership + `ICurrentUser` — the block actor is server-resolved), ADR 0003
  (`ApplicationUser`/domain-`User` shared key — blocks reference the domain `User`), ADR 0004 (free/local-only).
- Skills: `.claude/skills/security-hardening/SKILL.md`, `.claude/skills/ef-core-data-access/SKILL.md`,
  `.claude/skills/ef-core-migrations/SKILL.md`.

---

_Design-only ADR authored 2026-07-11 against the shipped Milestone-B1 code (verified: `UserBlock` entity +
`UserBlockConfiguration` unique `(BlockerId, BlockedUserId)` + `(BlockedUserId)` indexes + Restrict FKs;
`AddUserBlock` additive migration; `BlockQueries.AreBlockedEitherWayAsync`/`BlockedOrBlockedByIdsAsync`;
`BlockUserCommand` idempotent + silent + friendship-removing; `SendFriendRequestCommand` `Blocked` outcome +
neutral message; `GetProfileQuery` owner-blocks-viewer → 404 and viewer-blocks-owner → `BlockedByMe`). No git
performed; the human commits._
