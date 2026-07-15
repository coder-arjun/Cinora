# ADR 0012 — Activity Feed as a Keyset-Paginated Read-Time Query (no fan-out / materialized feed)

- **Status:** Accepted
- **Date:** 2026-07-03
- **Phase:** 3 (Reviews & Social), Milestone 3.4 (activity feed)
- **Deciders:** architecture-agent (design), performance-agent + database-agent (consulted), orchestrator (ratify)

## Context

Phase 3 turns the authenticated `/home` into a **friends feed**: a signed-in user sees their accepted
friends' recent social activity, newest-first, with infinite scroll. Phase 2 §7 reserved **keyset pagination
for local DB feeds** exactly for this moment (TMDB rails/search use TMDB's `page` param; our own feeds do not
use `OFFSET`).

Two shapes are on the table:

- **Read-time query (fan-in on read):** at request time, resolve the current user's friend set, then read the
  latest activity rows straight from the source tables (`Reviews`, and optionally `ReviewLikes`/`Comments`),
  ordered and keyset-paginated.
- **Materialized feed (fan-out on write):** maintain a denormalized `ActivityEvent` (or per-user feed) table,
  appended to whenever a friend acts, and read the feed as a single indexed scan.

The product intent (Implementation_Plan / PROGRESS) names "friends' recent reviews/likes/comments," i.e. a
**mixed-event** feed. Mixing three event types under a single time cursor is the hard part: three tables with
three timestamps cannot share one keyset without a `UNION` or a denormalized event row.

## Decision

**Model the Phase-3 feed as a read-time query with keyset pagination, and scope the Phase-3 feed to friends'
REVIEWS. Do NOT build a materialized/fan-out feed table. Add likes/comments as feed items via a bounded
`UNION` only if the product requires it, and defer the denormalized `ActivityEvent` table behind an explicit
trigger.**

### 1. Feed v1 = friends' reviews, read-time, keyset

- `GetActivityFeedQuery(FeedCursor? Cursor, int Take = 20) : IRequest<ActivityFeedVm>`; handler deps
  `IAppDbContext` + `ICurrentUser`. Steps:
  1. Resolve the current user's **accepted-friend ids** (see §2).
  2. Read `Reviews` where `UserId IN (friendIds)`, projected `AsNoTracking().Select(...)` to feed cards
     (author display name, title, rating, body excerpt, like/comment counts).
  3. **Keyset**, newest-first, on the stable composite `(CreatedAtUtc, Id)`:
     ```sql
     WHERE UserId IN (@friendIds)
       AND (CreatedAtUtc < @lastUtc OR (CreatedAtUtc = @lastUtc AND Id < @lastId))
     ORDER BY CreatedAtUtc DESC, Id DESC
     FETCH NEXT @take ROWS ONLY   -- take+1 to compute HasMore
     ```
     The cursor is an opaque encoding of `(lastUtc, lastId)` — never a page number, never `OFFSET`
     (deep-pagination cost; `dotnet-performance` rule).
- **Supporting index (migration delta, database-agent):** `IX_Reviews_UserId_CreatedAtUtc` on
  `(UserId, CreatedAtUtc)`. `Review` already has a unique `(UserId, MovieId)` and a standalone
  `(CreatedAtUtc)`; the feed's `UserId IN (...) ORDER BY CreatedAtUtc DESC` wants the composite so each
  friend's slice is index-ordered.
- **Friend-set bound:** the `IN (@friendIds)` list is bounded by a real friend cap (hundreds, not millions)
  and passed as a parameterized set; if it ever grows unreasonably it becomes the §4 trigger.

### 2. Friend-set resolution

- Accepted friendships are read from `Friends` where `Status = Accepted` and (`RequesterId = me` OR
  `AddresseeId = me`), projecting the *other* party's id. Supporting index (migration delta):
  `IX_Friends_AddresseeId_Status` on `(AddresseeId, Status)` for the reverse direction (the forward direction
  is served by the existing unique `(RequesterId, AddresseeId)` index).

### 3. Mixed-event feed (reviews + likes + comments) — bounded UNION, only if wanted

- If the product wants likes/comments as feed items, realize it as a **`UNION ALL` of three keyset-bounded
  projections** (each: friend-authored review created / friend liked a review / friend commented), each
  already filtered to the friend set and windowed to the cursor time, merged and ordered by event time. This
  stays a read-time query (no new table) and is a projection into a shared `FeedItemVm` discriminated by an
  `ActivityKind`. It is a heavier query than reviews-only and is therefore an **opt-in extension** for 3.4,
  not the default.

### 4. Deferred: the materialized `ActivityEvent` table — extract only on a trigger

Introduce a denormalized append-only `ActivityEvent` (fan-in read model, written by the same command handlers
that already persist the source row + its `Notification`) **only when** one fires:

1. The read-time `UNION` (or the reviews query) becomes a measured latency problem at realistic data volumes
   (performance-agent evidence), or
2. Cross-type ordering with a single clean cursor is needed at a scale the `UNION` cannot serve, or
3. The feed must include event types that have no natural single source row to scan.

Until then, YAGNI: a read-time query over indexed source tables is the right shape and avoids a write-path
fan-out (which doubles writes and needs backfill/repair for existing data).

## Consequences

**Positive**
- No write amplification, no backfill, no feed-consistency repair job — the feed is always exactly the source
  data. Simplest correct thing.
- Keyset pagination gives stable, cheap infinite scroll independent of depth; realizes the Phase-2 §7 reserve.
- Two small index additions; no new entity, no new migration risk beyond indexes.

**Negative / accepted**
- Reviews-only feed v1 does not show friends' likes/comments unless the §3 `UNION` extension is taken. Honest
  scope trade; flagged as a product/UX call in the design doc.
- A very large friend set would make `IN (@friendIds)` heavy — the §4 trigger, not a Phase-3 reality.
- The current user's *own* activity is excluded from the friends feed by design (the feed is "your friends");
  self-activity shows on the profile page.

## Alternatives considered

1. **Fan-out-on-write per-user feed table.** Rejected for Phase 3: write amplification (every review fans out
   to every friend's feed), backfill for existing rows, and consistency repair — heavy machinery for a
   single-instance app with a modest graph. Deferred behind the §4 trigger.
2. **Offset pagination (`Skip/Take`).** Rejected: deep-pagination cost and drift as new rows arrive
   (`dotnet-performance` / Phase-2 §7). Keyset only.
3. **Client-side merge of three separate endpoints.** Rejected: cannot paginate a merged timeline coherently;
   pushes ordering/keyset logic into the browser.

## Related
- ADR 0009 (`ICurrentUser` — the feed is scoped to the current user's friends), ADR 0011 (an optional
  `FeedHub` could later push live appends — deferred), ADR 0008 (command handlers as the write seam that a
  future `ActivityEvent` fan-in would hook into).
- `docs/architecture/phase-3-social-design.md` §8 (feed + keyset), §11 (migration deltas), §13 (performance).
- Phase-2 design §7 (keyset reserved for local feeds), `.claude/skills/dotnet-performance/SKILL.md`,
  `.claude/skills/ef-core-data-access/SKILL.md`.

---

_Design-only ADR authored 2026-07-03 (verified: `Review` has `HasIndex(CreatedAtUtc)` + unique
`(UserId, MovieId)`; `Friend` has unique `(RequesterId, AddresseeId)` + Restrict FKs; no feed/activity table
exists). No application code or migration written._
