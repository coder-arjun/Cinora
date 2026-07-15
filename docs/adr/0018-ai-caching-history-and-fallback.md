# ADR 0018 — AI Output Caching, `AIRecommendationHistory` Persistence, Hangfire Precompute, and the Outage Fallback

- **Status:** Accepted
- **Date:** 2026-07-03
- **Phase:** 5 (AI Recommendations), Milestones 5.2–5.3 (serve/cache/persist + precompute)
- **Deciders:** architecture-agent (serve/write split), backend-agent + database-agent + performance-agent (to
  implement), security-agent (dashboard auth, consulted), orchestrator + user (ratify standing up Hangfire)

## Context

The recommender must satisfy three hard requirements from the phase brief together:

1. **The page load makes no LLM call** — recommendations render from precomputed data only.
2. **Every run is recorded** (model, inputs/outputs summary, token usage) — the `AIRecommendationHistory`
   entity already exists (Phase 1: `Create(userId, model, inputSummary, outputSummary, promptTokens,
   completionTokens)`; index `(UserId, GeneratedAtUtc)`; `Restrict` FK to `User`; `SummaryMaxLength = 4000`).
2. **The page never 500s because of the AI** — a model outage, a cold start, or a broken JSON body must degrade,
   not error.

This requires deciding: *when* the model runs (never on a request), *where* the served set lives (a cache),
*what* is persisted (the existing history entity — not a parallel store), and *what* happens when there is no AI
output to serve (a deterministic fallback). ADR 0007 already deferred Hangfire and named **Phase 5 AI** as the
phase that would stand it up. Constraints: free/local-only (ADR 0004) — the free in-memory `IDistributedCache`
(no Redis/Azure) and Hangfire OSS + SQL Server storage (no Docker); CQRS purity (queries never mutate);
fail-closed authZ.

## Decision

**Precompute recommendations in a Hangfire job (never on a page view); serve them from `IDistributedCache` →
the latest `AIRecommendationHistory` row → a deterministic heuristic, in that order; persist every AI run to the
existing `AIRecommendationHistory` entity with token usage; and never let the AI 500 a page.**

### 1. Two paths, deliberately split (serving never touches the model)

- **Write path — `GenerateRecommendationsCommand` (a Command), run only by the Hangfire job.** It builds the
  profile + candidates (ADR 0017), calls `IRecommendationEngine.RankAsync` (ADR 0016), applies the hallucination
  guard, **persists one `AIRecommendationHistory` row**, and **warms the served cache**. It is the only writer.
- **Read path — `GetMyRecommendationsQuery` (CQRS-pure), run on every For-You render.** It resolves the served
  set with **no LLM call**. It is the only thing the For-You surfaces call.

### 2. Served cache — `IServedRecommendationCache` over free in-memory `IDistributedCache`

- Free provider only: `AddDistributedMemoryCache()` (ADR 0004/0007). An `IServedRecommendationCache` **port**
  (Application) with an Infrastructure adapter keeps serialization, the per-user key `cinora:ai:recs:{userId}`,
  the TTL (≈24 h, aligned to the nightly precompute), and **graceful degradation** out of the handlers — every
  read/write in try/catch → `LogWarning` → miss/no-op; a cache outage never surfaces as an error (the same SRP +
  degrade discipline as `CachedTmdbClient`, ADR 0007). **Invalidation:** TTL expiry; overwrite on the next
  precompute; optional eviction on a taste event.

### 3. Persistence — the existing `AIRecommendationHistory`, not a parallel store

- Written once per **successful** AI generation: `AIRecommendationHistory.Create(userId, model, inputSummary,
  outputSummary, promptTokens, completionTokens)` + `SaveChangesAsync`. `InputSummary` = a bounded, PII-free
  summary of the grounding inputs; `OutputSummary` = a **compact JSON envelope of the served set**, sized to fit
  the 4000-char column (`MaxResults` ≈12 picks, each with a capped `Reason`), making the row **self-contained**
  so the serve fallback can render straight from it. Token usage comes from the model's reported counts. The
  index `(UserId, GeneratedAtUtc)` serves the "latest row per user" fallback and the skip-unchanged check.
- **No new AI-output table.** The history entity is the durable audit **and** the fallback source; the cache is
  the fast serve store. The two are complementary, not redundant (mirroring the cache/DB split of ADR 0007).

### 4. Serve order (three tiers, LLM-free) — `GetMyRecommendationsQuery`

`userId = ICurrentUser.GetRequiredUserId()` (owner-scoped, ADR 0009):
1. **Cache hit** → the `RecommendationSet` (`Source = Ai`).
2. **Cache miss** → the latest `AIRecommendationHistory` row (`Top(1)` over the index) parsed from
   `OutputSummary` (`Source = Ai`).
3. **No history** → the **deterministic heuristic** (`Source = Heuristic`): "more like your highest-rated" /
   "popular in your favorite genres" / global-popular, computed live via the cached `ITmdbClient` — **no model**.
   Always returns a set so the page renders.

### 5. Precompute — Hangfire OSS + SQL Server storage (redeems ADR 0007)

- `RecommendationPrecomputeJob.RunAsync(ct)` recurring `Cron.Daily(4)` UTC: select **active users**, **skip
  unchanged taste** (a taste hash vs the newest history row), else `sender.Send(GenerateRecommendationsCommand
  (userId))`. The **job is the orchestrator** (it may inject `ISender` — the ADR-0008 "controller/job
  orchestrates, handler stays pure" pattern; not `ISender` inside a handler). Idempotent + re-runnable (Hangfire
  retries); `[DisableConcurrentExecution]`; per-user failures caught so one bad user does not abort the batch.
  Free Hangfire OSS + `UseSqlServerStorage` on the existing `CinoraDb` — **no Docker, no paid queue**.
- **Dashboard fail-closed:** `MapHangfireDashboard("/jobs")` gated by an admin `IDashboardAuthorizationFilter`
  (never anonymous; environment-name-exact). Optional event-driven refresh (after a rating/watchlist-`Watched`)
  via an `IRecommendationRefreshScheduler` **port** so write handlers never reference Hangfire — **flagged**,
  nightly-only ships by default.

### 6. Never 500 — the fallback is mandatory

- An engine failure in the job (timeout/parse/transport → `RecommendationEngineException`) is logged, writes
  **no** history row, and leaves the prior cache/history intact; the job MAY cache a **heuristic** set (shorter
  TTL) so the user still gets something honest. The serve path degrades independently (tier 3). The page renders
  regardless of the model's health.

## Consequences

**Positive**
- **The page is instant and LLM-free** — a cache/DB read on every render; the model runs only in the batch job.
  Directly satisfies the exit criteria (no page-load model call; every run recorded with tokens; fallback works
  with the AI disabled).
- **The existing schema suffices** — no new entity/column for the core path; the history entity is reused as
  designed (Phase 1).
- **Graceful under every failure** — cache outage → history; history gap → heuristic; model down → heuristic; no
  path errors.
- **Hangfire lands exactly when it is first needed** (ADR 0007's stated trigger), on free SQL storage, with a
  secured dashboard.

**Negative / accepted costs**
- **Recommendations can be up to a refresh cycle stale** (nightly). Mitigated by seen-exclusion (acted-on titles
  drop next run) + optional event-driven refresh; accepted default is nightly freshness.
- **`OutputSummary` is bounded to 4000 chars** — the envelope caps picks/reasons to fit; a larger served set
  would need the re-hydrate-from-cache variant (store `(t,m,r)`, re-hydrate poster/title at serve). Flagged.
- **In-memory cache is per-process** (ADR 0004) — an eviction/restart falls to the history row; acceptable for a
  single-instance local build.
- **Hangfire is new securable infra** — a real attack surface, mitigated by the admin-gated, fail-closed
  dashboard and bare-`Guid` job args.
- **A heuristic set is not audited in `AIRecommendationHistory`** (no model/tokens to record) — the AI-vs-
  heuristic distinction lives in the cache/`OutputSummary` `"src"` marker, not a DB column. Accepted.

## Amendment (2026-07-06) — skip-unchanged is timestamp-based (not a "taste hash"); dashboard match is user-id-or-email

_Recorded at the Phase 5 architecture exit gate (PASS, 0 Critical / 0 High). A doc-only reconciliation to the
shipped `RecommendationPrecomputeJob` + admin dashboard filter — **no behavior change**. It **supersedes** the
"taste hash" wording in §5 (and §3's "skip-unchanged check") and sharpens §5's generic "admin filter"; the
original text is retained above as the design intent._

**1. Skip-unchanged compares timestamps, not a stored taste hash.** §5 (and §3) proposed skipping an unchanged
user via a **taste hash** (a hash over the ordered top-rated / watchlist tuples) carried inside the history row.
The shipped `RecommendationPrecomputeJob.SelectStaleUsersAsync` instead compares the user's **latest
`AIRecommendationHistory.GeneratedAtUtc` against their latest review/watchlist activity high-water mark**,
inside a **`RecommendationOptions.PrecomputeLookbackDays` = 45-day** active-user window: a user is *stale*
(regenerated) iff they have in-window review/watchlist activity AND (no history row OR their newest history
predates that activity). This is **functionally sound and strictly more conservative** than a taste hash — a
timestamp comparison can only ever *over*-regenerate (an idempotent re-touch that leaves taste unchanged still
moves a timestamp) and can **never** serve a stale set after a real taste change, which is the property that
matters. It also needs **no schema change** (the existing `(UserId, GeneratedAtUtc)` index serves it), keeping
Phase 5 schema-stable.

**Accepted trade-off (the honest edge).** A generation that yields an **empty** set (thin profile, no
candidates, engine/TMDB outage, or an all-hallucinated result) writes **no** `AIRecommendationHistory` row — so
a persistently-empty user stays "stale" and is **re-attempted on every run while still inside the lookback
window**. That re-attempt is desirable (it retries a transient engine/TMDB outage) and self-bounds (the user
ages out of the window). The **durable taste-hash / attempt marker remains the recorded future refinement** —
warranted only if persistently-empty-cohort re-attempts ever matter at scale (a durable per-user hash or
attempt store unioned into the skip check; REVIEW_BACKLOG Milestone 5.3 row).

**2. Dashboard admin gate matches user-id or email (name-claim arm dropped).** §5's admin
`IDashboardAuthorizationFilter` shipped as the thin `AdminDashboardAuthorizationFilter` → the pure, unit-tested
`AdminAccessPolicy`, gated by `AdminDashboardOptions.AdminAccounts` (config section `AdminDashboard`). It is
**fail-closed** (an empty allow-list denies *every* caller — anonymous and authenticated alike — in *every*
environment, Production included) and admits an authenticated caller only when their **server-assigned user-id
(`ClaimTypes.NameIdentifier`, the `ApplicationUser.Id` GUID) OR their email claim** is on the allow-list,
matched case-insensitively (`StringComparer.OrdinalIgnoreCase`). The free-form **name claims (`Identity.Name` /
`ClaimTypes.Name`) are deliberately NOT matched** — they are not server-minted and would couple authZ to the
`UserName == Email` invariant — a hardening beyond §5's generic "admin filter" wording (Milestone 5.3 L1/L2). A
`Development`-only "any authenticated user" convenience flag (`AllowAnyAuthenticatedInDevelopment`) exists and
defaults **off**.

## Alternatives considered

1. **Generate synchronously on the For-You page render.** Simplest wiring. **Rejected:** violates the exit
   criterion (page-load model call), adds seconds of tail latency, and 500s on a model outage.
2. **A new denormalized recommendation table (per-title rows).** Structured querying. **Rejected:** the existing
   `AIRecommendationHistory` + the served cache already cover audit + serve; a parallel store is the "invent a
   parallel store" anti-pattern the phase brief forbids. (A separate `RecommendationFeedback` entity for durable
   "not interested" is a *different*, deferred question — Phase-5 design §11.)
3. **Redis/Azure for the served cache.** Cross-instance sharing. **Rejected:** beyond the free in-memory default
   (ADR 0004); the `IDistributedCache` seam keeps a Memurai/Redis swap a registration-only change later.
4. **No fallback (serve nothing / an error when the model is down).** Less code. **Rejected:** the page must
   never 500 from the AI (exit criterion); the deterministic heuristic is mandatory.
5. **Ship the event-driven per-user refresh in Phase 5 core.** Fresher recs. **Deferred** to a flagged
   refinement — the nightly job meets the requirement; event-driven adds a scheduler port + debounce complexity
   better validated once the nightly path is proven.

## Related
- ADR 0007 (deferred Hangfire, named Phase-5 AI as its trigger — redeemed here; the cache/DB complementary split)
- ADR 0016 (the engine + `RecommendationEngineException` that this fallback catches), ADR 0017 (the candidate
  fan-out the heuristic reuses)
- ADR 0004 (free in-memory cache + Hangfire on free SQL storage, no Docker), ADR 0008 (the job orchestrates like
  a controller — no `ISender` in a handler), ADR 0009 (owner-scoped serve via `ICurrentUser`)
- `docs/architecture/phase-5-ai-design.md` (§6 persistence, §7 caching/serve, §9 fallback, §10 Hangfire)
- Code: `src/Cinora.Domain/Entities/AIRecommendationHistory.cs`,
  `src/Cinora.Infrastructure/Persistence/Configurations/AIRecommendationHistoryConfiguration.cs`,
  `src/Cinora.Application/Common/Interfaces/IAppDbContext.cs`
- Skills: `.claude/skills/hangfire-background-jobs/SKILL.md`, `.claude/skills/redis-caching/SKILL.md`,
  `.claude/skills/openai-recommendations/SKILL.md`, `.claude/skills/error-handling-logging/SKILL.md`

---

_Design-only ADR (2026-07-03): no code written. Verified against Phase 1–4: `AIRecommendationHistory` entity +
`AIRecommendationHistoryConfiguration` (`(UserId, GeneratedAtUtc)` index, `Restrict` FK, `SummaryMaxLength=4000`);
`IAppDbContext.AIRecommendationHistories` + `SaveChangesAsync`; `AddDistributedMemoryCache()` +
`IDistributedCache` (Phase 2); `ICurrentUser` (ADR 0009); no Hangfire/`RecommendationPrecomputeJob`/served-cache
type exists in `src/`._

_Amendment (2026-07-06) verified against shipped Phase-5 code: `RecommendationPrecomputeJob.SelectStaleUsersAsync`
(timestamp-vs-activity selection within `RecommendationOptions.PrecomputeLookbackDays = 45`);
`AdminAccessPolicy.IsAuthorized` / `AdminDashboardAuthorizationFilter` / `AdminDashboardOptions.AdminAccounts`
(user-id-or-email, `OrdinalIgnoreCase`, fail-closed, name-claim arm dropped). No behavior changed. Last verified
against code: 2026-07-06._
