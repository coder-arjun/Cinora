# ADR 0008 — TMDB Catalog Persistence via `ISender` Commands, not an `ITitleCatalog` Port

- **Status:** Accepted
- **Date:** 2026-07-02
- **Phase:** 2 (Discovery), Milestone 2.2 (catalog persistence)
- **Deciders:** backend-agent + architecture-agent (realization), orchestrator (ratify at the Phase-2
  exit-layer review), documentation-agent (record the governance gap)

## Context

The Phase-2 design proposed a single Infrastructure persistence seam — an **`ITitleCatalog` port**
(`Cinora.Application/Common/Interfaces/ITitleCatalog.cs`) exposing `EnsureTitleAsync` and
`SyncGenresAsync`, implemented by `Cinora.Infrastructure/Catalog/TitleCatalog.cs` and registered as
`services.AddScoped<ITitleCatalog, TitleCatalog>()`. This port is named in **ADR 0006** (Decision
data-flow diagram and the "Domain is untouched on reads" bullet, which route persistence "via
`ITitleCatalog`") and throughout **`docs/architecture/phase-2-discovery-design.md`** (§1 component table,
§2.1 data-flow diagram, §5 feature-vertical table, §9.2 milestone deliverable). ADR 0007 (the
catalog-persistence ADR) already described the seam as **two commands**, so the design record was
internally inconsistent about whether the seam is a port or a pair of commands.

The **shipped** code realizes the seam as two `ISender` commands, not a port:

- `src/Cinora.Application/Features/Catalog/SyncGenresCommand.cs` —
  `SyncGenresCommand : IRequest<SyncGenresResult>`, handled by
  `SyncGenresCommandHandler(IAppDbContext db, ITmdbClient tmdb)`.
- `src/Cinora.Application/Features/Catalog/EnsureTitleCachedCommand.cs` —
  `EnsureTitleCachedCommand(int TmdbId, MediaType MediaType) : IRequest<EnsureTitleCachedResult>`
  (with an `EnsureTitleCachedCommandValidator` requiring `TmdbId > 0`), handled by
  `EnsureTitleCachedCommandHandler(IAppDbContext db, ITmdbClient tmdb)`.

Both handlers inject **`IAppDbContext` directly** — no repository, no `ITitleCatalog` port. The
`ITitleCatalog` / `TitleCatalog` types were **never created**; a repository-wide search finds no such
file. The Phase-2 design doc states plainly that "deviations require an ADR," and none was recorded when
the port was dropped in favour of the commands. This ADR closes that governance gap.

Realizing the seam as commands is consistent with the standing Phase-1 boundary policy
(`docs/architecture/solution-structure.md` §5 anti-patterns and §7 CQRS boundary): **no generic
`IRepository<T>`; repositories/abstractions are reserved for aggregate roots with genuine invariants and
introduced only when a real use case needs them.** `Movie`/`Genre`/`MovieGenre` are TMDB-cache
projections keyed by external ids, not invariant-bearing aggregate roots. With exactly **two** consumers
— both of which are themselves testable command handlers — an `ITitleCatalog` port sits between the
handlers and `IAppDbContext` as pure indirection that adds no present value: it would not simplify
testing (the handlers are already faked at `ITmdbClient` and run against a real provider / in-memory
context), and it abstracts a boundary that has no second implementation and no invariant to protect.

## Decision

**Persist the TMDB catalog through the two `ISender` commands; defer the `ITitleCatalog` port.**

- **Genres** are synchronised by `SyncGenresCommand`. Its handler unions TMDB's movie + TV genre lists
  (de-duplicated by TMDB id), reads the ids already present in one indexed `AsNoTracking` projection, and
  **inserts only the missing genres** (`Genre.Create(...)`), never mutating existing rows. It is
  idempotent and race-safe by the **`Genre.TmdbGenreId` unique index**
  (`GenreConfiguration`: `HasIndex(g => g.TmdbGenreId).IsUnique()`). It returns
  `SyncGenresResult(int Inserted, int AlreadyPresent)`.
- **Titles** are cached on first touch by `EnsureTitleCachedCommand`. Its handler probes the local
  `Movie` table directly (a cheap `AsNoTracking` existence check, never the TMDB cache); on a hit it
  returns the existing internal id; on a miss it fetches details from `ITmdbClient`, maps the read model
  via `Movie.FromTmdb(...)`, links genres via `MovieGenre.Link(...)`, and saves. It is idempotent and
  **race-safe by the `(TmdbId, MediaType)` unique index**
  (`MovieConfiguration`: `HasIndex(m => new { m.TmdbId, m.MediaType }).IsUnique()`): a concurrent insert
  that trips the index is caught (`DbUpdateException`), re-probed, and reported as already-cached rather
  than surfaced as an error. A TMDB 404 is **not** persisted. It returns
  `EnsureTitleCachedResult(EnsureTitleOutcome {Created | AlreadyCached | NotFound}, Guid? MovieId)`.
- **The `ITitleCatalog` port is deferred, not rejected on principle.** Extract it later only when the
  trigger below fires.

## Consequences

**Positive**
- **Handler purity is preserved.** Each handler depends only on `IAppDbContext` + `ITmdbClient` and does
  one thing; there is no `ISender`-inside-a-handler and no port indirection. This matches ADR 0007's
  "the controller (not a handler) dispatches `EnsureTitleCachedCommand`" and the Phase-2 design §5 rule
  "no `ISender` inside a handler."
- **Fewer types for two consumers.** No port interface, no adapter class, no DI registration to maintain
  — consistent with §5/§7 "no abstraction without a present use case."
- **Idempotency and race-safety live at the database**, on the two unique indexes that already exist
  since Phase 1, so repeated or concurrent runs converge without duplicate rows.

**Negative / accepted costs**
- **Cross-command reuse is via controller orchestration, by design.** When a later phase needs the
  internal `Movie.Id` before doing more work, the **controller** dispatches `EnsureTitleCachedCommand`,
  reads `EnsureTitleCachedResult.MovieId`, then dispatches the next command — the handler never calls
  `ISender` or a catalog port internally. **(Planned — Phase 3/4.)** No controller dispatches these
  commands yet: the Phase-2 Details wiring (milestone 2.5) and the Phase-3 `CreateReviewCommand` /
  Phase-4 `AddToWatchlistCommand` orchestration are not yet built. This ADR fixes the *pattern* those
  consumers must follow, not shipped wiring.
- **A small genre-upsert duplication exists between the two handlers.** `SyncGenresCommand` upserts
  genres by `TmdbGenreId`, and `EnsureTitleCachedCommand.EnsureGenresAsync` independently upserts any genre
  a title references but that is missing locally (so a link never fails when genres were not pre-synced).
  The two upsert paths are near-identical; an `ITitleCatalog` port (or a shared internal helper) would
  DRY them. This is accepted for now as the price of keeping the handlers self-contained, and is one of
  the triggers below.

## Phase-3 trigger — extract the port only when one of these fires

Introduce `ITitleCatalog` (or a shared catalog service) **only** when the abstraction earns its keep:

1. A **handler-internal consumer** needs ensure-persist — i.e. a handler (not a controller) legitimately
   needs the internal `Movie.Id` mid-flow and controller orchestration cannot cleanly supply it; or
2. A **third consumer** of the persistence seam appears (beyond the two commands); or
3. The **duplicated genre-upsert** above becomes worth consolidating behind one seam; or
4. **`Movie` / `Genre` stop being TMDB-cache projections and acquire genuine domain invariants** — i.e.
   they become true aggregate roots that must protect their own rules rather than external-id-keyed read
   caches — at which point a repository/port for that aggregate is warranted per
   `solution-structure.md` §7 ("introduce one only for an aggregate root with genuine invariants when its
   first real use case appears").

Until then, YAGNI (§5/§7): two testable command handlers on `IAppDbContext` are the right shape.

## Alternatives considered

1. **`ITitleCatalog` Infrastructure port (`EnsureTitleAsync`/`SyncGenresAsync`) — the original design.**
   A single named persistence seam, DRYs the genre upsert, and gives Phase-3/4 handlers an injectable
   dependency. **Deferred:** with only two consumers (both command handlers) and no invariant to protect,
   the port is indirection without present value and would violate the §5/§7 "no abstraction without a
   use case" rule; the commands are already independently testable. Revisited when a Phase-3 trigger
   above fires.
2. **Persist inside the Details *query* (mutate-on-read).** **Rejected** (also by ADR 0007): violates
   CQRS query purity; persistence is a command the controller dispatches after the read.
3. **A generic `IRepository<Movie>` / `IRepository<Genre>`.** **Rejected:** the §5 hard ban on generic
   repositories; `Movie`/`Genre` are cache projections, not aggregate roots with invariants.

## Related
- ADR 0006 (TMDB integration boundary — the read models these commands persist; amended to point its
  persistence-seam references here)
- ADR 0007 (TMDB caching & catalog persistence — already describes the two commands; amended with a
  forward pointer here)
- ADR 0001 (layering — ports in Application, adapters in Infrastructure), ADR 0005 (hand-rolled
  `ISender` — the mediator these commands run through)
- `docs/architecture/phase-2-discovery-design.md` (§1/§5 reconciled to name the commands; ADR 0008 added
  to its ADR list), `docs/architecture/solution-structure.md` (§5 anti-patterns, §7 CQRS boundary)
- Skills: `.claude/skills/cqrs-mediatr/SKILL.md`, `.claude/skills/clean-architecture-dotnet/SKILL.md`,
  `.claude/skills/ef-core-data-access/SKILL.md`

## Amendments

- **2026-07-02 (Milestone 2.2 catalog fix-round + its H-1 fix; reviewed and endorsed by the architecture
  gate).** The `EnsureTitleCachedCommand` persistence mechanics evolved from a single `SaveChanges` to a
  **two-committed-step** model. The Decision above — persist the TMDB catalog through the two `ISender`
  commands and defer the `ITitleCatalog` port — is **unchanged**; only the intra-handler persistence shape
  changed. The points below supersede the single-save wording in the Decision.

  1. **Two-committed-step persistence for `EnsureTitleCached`.** The handler now persists in two
     independently committed `SaveChanges` calls: **step 1** `EnsureGenresAsync(...)` upserts any missing
     `Genre` rows in its OWN race-tolerant `SaveChanges` (on a genre-race `DbUpdateException` it logs,
     calls `IAppDbContext.DiscardPendingChanges()`, then reloads the committed ids); **step 2** `Handle`
     writes the `Movie` and its `MovieGenre` links via `LinkGenres(...)` in a second `SaveChanges` (the
     existing `(TmdbId, MediaType)` race catch is retained). **Why this is preferred over a single wrapping
     `IDbContextTransaction`:** a genre-insert race must not roll back or rethrow the title write; the title
     and its links stay atomic *within* step 2; and because the whole command is idempotent and
     re-runnable, orphan genres left by a crash *between* the two commits are harmless (a later re-run links
     them). A single wrapping transaction would couple the two steps so a benign genre race could abort the
     title write — the opposite of the intended behaviour.

  2. **New port affordance `IAppDbContext.DiscardPendingChanges()`** — implemented in `CinoraDbContext` as
     `ChangeTracker.Clear()`, which detaches ALL pending tracked entities. **Why it was needed:** EF Core
     keeps failed inserts in the `Added` state after a failed `SaveChanges`; without detaching them, step
     2's title `SaveChanges` re-flushed the leftover `Added` genres and re-tripped the unique
     `Genre.TmdbGenreId` index, rolling back the title write and surfacing as a **500** — the review's
     **High** finding (H-1). Detaching the staged genres before reloading is safe and scoped here because
     at that point the genres are the only tracked work (the movie and its links are added later, in step
     2). This slightly widens the Application port surface by one method; the widening is judged justified
     and minimal.

  3. **Stale method-name correction.** Earlier text referred to `LinkGenresAsync`. The shipped methods are
     `EnsureGenresAsync` (the race-tolerant genre upsert, step 1) and `LinkGenres` (a synchronous,
     no-`SaveChanges` linker, step 2) — there is **no** `LinkGenresAsync`. The "genre-upsert duplication"
     consequence bullet above was corrected to name `EnsureGenresAsync`.

  4. **F3 not-found contract broadened.** `EnsureTitleOutcome.NotFound` now covers BOTH a TMDB **404**
     (null details) AND a title with **no resolvable name** (TMDB sent neither `title` nor `name`, so the
     mapped `Title` is blank and would trip `Movie.FromTmdb`'s `Guard.Required`). Neither is persisted; both
     resolve to `NotFound`. This supersedes the Decision's narrower "a TMDB 404 is not persisted" wording.

  5. **Genre-race logging.** Both genre-race catches — in `EnsureGenresAsync` and in
     `SyncGenresCommandHandler` — now emit the source-generated Warning `CatalogLog.GenreInsertRaceReconciled`
     (EventId 2200) so a `DbUpdateException` that is NOT actually a benign genre race stays observable
     rather than silently swallowed. `SyncGenresCommandHandler.Handle` and `ReconcileAfterRaceAsync` are
     otherwise unchanged; they need no `DiscardPendingChanges()` because there is no second save (the
     context is disposed with the request and the sync re-runs idempotently).

  6. **Testing decision (race path).** The deterministic race tests now run on **SQLite in-memory** (a
     single shared open connection) — a real relational engine that ENFORCES the unique `Genre.TmdbGenreId`
     and `(TmdbId, MediaType)` indexes, so a duplicate insert throws a genuine `DbUpdateException`. **EF
     Core InMemory was removed**: it cannot enforce unique indexes and therefore *hid* the H-1 High finding;
     this also aligns with the `integration-testing` / `xunit-testing` "never EF InMemory" rule. A **scoped
     `NuGetAuditSuppress`** for advisory `GHSA-2m69-gcr7-jv3q` (in
     `tests/Cinora.Application.Tests/Cinora.Application.Tests.csproj` only) covers the test-only SQLite
     native (`SQLitePCLRaw.lib.e_sqlite3`); only the vulnerable native is in the offline NuGet cache, the
     engine never ships in any runtime artifact, and the suppression is a documented, test-only,
     **temporary** exception (removal tracked in `REVIEW_BACKLOG.md`).

---

_Last verified against code: 2026-07-02 (re-verified after the Milestone 2.2 fix-round) —
`src/Cinora.Application/Features/Catalog/SyncGenresCommand.cs` and `EnsureTitleCachedCommand.cs`
(handlers inject `IAppDbContext` + `ITmdbClient` + `ILogger` directly; no port). `EnsureTitleCachedCommandHandler`
persists in two committed steps (`EnsureGenresAsync` then `LinkGenres`; there is no `LinkGenresAsync`), and
`NotFound` covers a TMDB 404 (null details) and a blank title (`string.IsNullOrWhiteSpace(details.Title)`).
`src/Cinora.Application/Common/Interfaces/IAppDbContext.cs` declares `DiscardPendingChanges()`, implemented in
`src/Cinora.Infrastructure/Persistence/CinoraDbContext.cs` as `ChangeTracker.Clear()`.
`src/Cinora.Application/Features/Catalog/CatalogLog.cs` defines `GenreInsertRaceReconciled` (Warning, EventId 2200).
`MovieConfiguration.cs` (unique `(TmdbId, MediaType)` index) and `GenreConfiguration.cs` (unique `TmdbGenreId`
index) unchanged; no `ITitleCatalog`/`TitleCatalog` type exists in `src/`; no controller dispatches these
commands yet (only `DiagnosticsController` dispatches `PingCommand`)._
