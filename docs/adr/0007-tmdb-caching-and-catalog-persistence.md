# ADR 0007 — TMDB Caching (In-Memory, Cache-Aside Decorator) and Catalog Persistence Strategy

- **Status:** Accepted
- **Date:** 2026-07-02
- **Phase:** 2 (Discovery), Milestones 2.1–2.2 (refresh deferral spans later phases)
- **Deciders:** architecture-agent (data-flow), backend-agent + database-agent + performance-agent (to
  implement), orchestrator + user (to ratify the Hangfire deferral)

## Context

Phase 2 serves Home/Search/Details from TMDB. Two data-management questions must be settled together,
because they define one coherent read/write data flow:

1. **Caching** — TMDB is rate-limited and latency-variable; repeat views (home rails, popular details)
   must not hit TMDB every time. What caches what, with which keys/TTLs, on which free backend?
2. **Catalog persistence** — the schema has `Movie`/`Genre`/`MovieGenre` (with a unique
   `(TmdbId, MediaType)` index) from Phase 1. Phase 3–4 entities (`Review`, `Watchlist`, `Comment`)
   reference titles by the **internal `Movie.Id` (Guid)**, not by TMDB id. *When* are those rows written,
   and how do they coexist with the cache? Is a background refresh (Hangfire) in scope now?

Constraints: free/local-only (ADR 0004) — in-memory `IDistributedCache`, **no Redis/Azure/Docker**; the
TMDB boundary of ADR 0006; CQRS purity (queries never mutate, cqrs-mediatr skill); and YAGNI applied to
architecture (don't stand up Phase 3+/5 machinery in Phase 2).

## Decision

### Caching — cache-aside in a decorator, on the free in-memory provider

- **Backend: `AddDistributedMemoryCache()` only.** The free in-memory `IDistributedCache`; a
  Redis-compatible provider (Memurai) is a later, registration-only swap and **out of scope**.
- **Cache-aside lives in a decorator, not in handlers.** `CachedTmdbClient : ITmdbClient` wraps the raw
  `TmdbClient` (manual DI decoration — no Scrutor). Handlers call `ITmdbClient` and are oblivious to
  caching (SRP: raw client = HTTP + map; decorator = cache-aside + graceful degradation). Cached values
  are the **Application read models** (ADR 0006), so a hit is identical to a live call — transparent.
- **Key convention `cinora:tmdb:{resource}:{media}:{args}`** with per-resource TTLs: trending 1 h,
  popular 3 h, top-rated 12 h, details 24 h, genres 24 h, search 15 min — **±10% jitter** on the shared
  hot rail keys to avoid synchronized expiry (stampede). Search keys are **normalized** (trim + lowercase)
  and short-TTL'd to bound cardinality.
- **Never cache failures; degrade, never error.** Only successful, non-null payloads are stored (a 404/null
  details response and search misses are not cached). Every read/write is wrapped in try/catch →
  `LogWarning` → fall through to TMDB. **A cache outage must never surface as an error page.**

### Catalog persistence — genres eager, titles read-through on first touch, refresh deferred

- **Genres: persisted eagerly (Phase 2).** `SyncGenresCommand` idempotently upserts by `TmdbGenreId` from
  `/genre/movie/list` + `/genre/tv/list`, superseding the Phase 1 static `GenreSeedData`. Justified because
  genres are a tiny bounded set and are **read** this phase to resolve `genre_ids` → names on cards (TMDB
  list/search payloads carry only ids).
- **Movies + `MovieGenre`: read-through, first-touch on the Details view.** An idempotent
  **`EnsureTitleCachedCommand(MediaType, TmdbId)`** — a *command*, dispatched by the Details controller
  **after** the display query — existence-checks by the `(TmdbId, MediaType)` unique index (one cheap
  indexed read in steady state) and, on miss, maps the cached read model → `Movie.FromTmdb(...)` +
  `MovieGenre.Link(...)`. Rails/search never persist. This is the **one canonical TMDB→internal-`Guid`
  seam** that Phase 3 (`CreateReviewCommand`) and Phase 4 (`AddToWatchlistCommand`) reuse.
- **Cache and DB are complementary, not redundant.** Cache = display-latency accelerator (source: TMDB);
  DB = durable internal-identity anchor (source of the internal `Guid`) + the genre id→name map. In
  Phase 2, **display reads never query the `Movie` table** — it is write-mostly, populated for Phase 3–4.
  Local-catalog **search merge** ("TMDB + local catalog") is **deferred**: the Phase 2 catalog is too
  sparse to add value; Phase 2 search stays TMDB-only.
- **CQRS purity preserved:** the only writes are the two commands; queries never `SaveChangesAsync`; the
  controller (not a handler) dispatches `EnsureTitleCachedCommand` after the read (no `ISender` inside a
  handler).

### Background refresh (Hangfire) — DEFERRED out of Phase 2

- **Defer the nightly `TmdbSyncJob`.** In Phase 2 nothing reads persisted movie metadata, so "stale
  metadata" has no user-visible effect, and the 24 h details cache TTL already keeps the display path
  fresh. Standing up Hangfire (a dependency, a server, a dashboard to secure, idempotent jobs) to refresh
  rows no feature reads is premature (YAGNI).
- **When it returns:** the phase that first *consumes* persisted metadata (Phase 3 renders title data
  from the local `Movie` row; or Phase 5 AI needs a populated catalog). Then **Hangfire OSS + SQL Server
  storage** (free, no Docker) is the right fit: idempotent upsert by `(TmdbId, MediaType)`, UTC
  `Cron.Daily`, `Movie.CachedAtUtc` as the staleness marker, admin-gated dashboard.

## Consequences

**Positive**
- Repeat views are served from process memory at zero cost; TMDB rate limits are respected; a cache
  outage degrades gracefully.
- The decorator keeps caching out of handlers and out of the raw client — each is independently testable
  (handler with a fake client; decorator with a fake inner + real in-memory cache).
- Exactly the titles users engage with get internal rows; the catalog does not bloat with never-touched
  cards. Phases 3–4 inherit one proven persistence seam.
- Phase 2 carries no Hangfire surface it does not need.

**Negative / accepted costs**
- **In-memory cache is per-process** (ADR 0004): no cross-instance sharing; fine for a single-instance
  local build (every value is reconstructable from TMDB).
- **A details GET performs a write** (idempotent upsert) — mitigated by the indexed existence check and by
  keeping it a command; movable behind a background enqueue once Hangfire lands.
- **The phase brief's exit criterion "nightly Hangfire job registered/runnable"** is not met by this
  design as written — flagged for the user to ratify; **optional Milestone 2.6** honors it if desired.
- Phase 2's local `Movie` table is written but not read this phase (deliberate — it serves Phase 3–4).

## Alternatives considered

1. **Cache-aside inline in each handler.** Fewer types. **Rejected:** repeats the try/catch/TTL/degrade
   logic in every handler and couples handlers to caching; the decorator centralizes it (SRP).
2. **Real Redis/Memurai now.** Cross-instance cache + sorted sets/counters. **Rejected:** unnecessary for a
   single-instance local build and beyond the free in-memory default (ADR 0004); the `IDistributedCache`
   seam keeps the swap cheap later.
3. **Persist every rail/search card (eager full-catalog import).** A rich local catalog immediately.
   **Rejected:** bloats the DB with titles no one interacts with and multiplies writes on every browse.
4. **Persist movies inside the Details *query* (mutate-on-read).** One dispatch. **Rejected:** violates
   CQRS query purity (queries never mutate); persistence is a command dispatched by the controller.
5. **Defer movie persistence entirely to Phase 3.** Strictest YAGNI. **Rejected (narrowly):** Phase 3
   review-create would still need the ensure-persisted seam, so defining and first-exercising it here (on
   the natural details trigger) de-risks Phase 3 without speculative abstraction. (Recorded as the
   fallback if the orchestrator prefers zero Phase 2 movie writes.)
6. **Ship the nightly Hangfire job in Phase 2 (per the brief).** Honors the exit criterion. **Deferred:**
   no Phase 2 consumer of the refreshed data; introduces securable infra prematurely. Available as
   optional Milestone 2.6.

## Related
- ADR 0004 (free-only — in-memory cache, Hangfire-when-needed on free SQL storage, no Docker)
- ADR 0006 (TMDB boundary — the read models this decorator caches and the persistence path maps to Domain)
- ADR 0008 (how the two persistence commands are realized — `ISender` commands on `IAppDbContext`, not an
  `ITitleCatalog` port)
- ADR 0001 (layering — cache/catalog adapters in Infrastructure, ports in Application)
- `docs/architecture/phase-2-discovery-design.md` (§3 caching, §4 persistence, §9 milestones 2.1/2.2/2.6)
- Skills: `.claude/skills/redis-caching/SKILL.md`, `.claude/skills/ef-core-data-access/SKILL.md`,
  `.claude/skills/hangfire-background-jobs/SKILL.md`, `.claude/skills/dotnet-performance/SKILL.md`
- Code (Phase 1, consumed here): `src/Cinora.Domain/Entities/{Movie,Genre,MovieGenre}.cs`,
  `src/Cinora.Infrastructure/Persistence/Configurations/MovieConfiguration.cs` (the `(TmdbId, MediaType)`
  unique index), `src/Cinora.Infrastructure/Options/CacheOptions.cs`

## Amendments

- **2026-07-02 (Phase-2 exit-layer reconciliation):** this ADR already described the persistence seam as
  the **two commands** `SyncGenresCommand` and `EnsureTitleCachedCommand`, and the shipped code matches
  that — the handlers persist via `IAppDbContext` directly, with **no `ITitleCatalog` port** (that port,
  named in ADR 0006 and the Phase-2 design doc, was never built and is deferred). **ADR 0008** now
  formally records the commands-over-port decision, its idempotency/race-safety on the `(TmdbId,
  MediaType)` and `TmdbGenreId` unique indexes, and the Phase-3 trigger for extracting a port later. This
  ADR's caching and persistence decisions are unchanged; the pointer to ADR 0008 is additive.

---

_Design-only ADR (2026-07-02): no code written. Verified against Phase 1: `Movie`/`Genre`/`MovieGenre`
entities and factories, `MovieConfiguration` unique `(TmdbId, MediaType)` index, `Movie.CachedAtUtc`,
`CacheOptions` (Provider=Memory default, DefaultTtlSeconds), `IAppDbContext` DbSets._
