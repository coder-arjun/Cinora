# ADR 0017 — Grounded Candidate-Generate-then-Rank, with a Hallucination Guard

- **Status:** Accepted
- **Date:** 2026-07-03
- **Phase:** 5 (AI Recommendations), Milestone 5.1 (the correctness core)
- **Deciders:** architecture-agent (pipeline design), ai-agent + backend-agent (to implement),
  performance-agent (candidate caps, consulted)

## Context

An LLM asked, bare, to "recommend movies for this user" returns a plausible-looking list that is unreliable in
two ways that matter to Cinora:

1. **Hallucination.** The model invents titles that do not exist, or returns real titles whose exact string
   does not resolve to a TMDB id — so a card links nowhere, or a watchlist/review write against it fails.
2. **Generic output.** Without the user's real taste it drifts to a global top-100 list; and resolving free-text
   titles back to the catalog requires fuzzy string matching (ambiguous, remake-prone, locale-sensitive).

Cinora must serve **explainable** recommendations where **every suggested title is a real, resolvable TMDB
title** (the phase exit criterion: "hallucinated titles filtered out"). The catalog anchor is the internal
`Movie` row (keyed by the unique `(TmdbId, MediaType)` index, ADR 0007/0008); titles are addressed on every
surface by `(tmdbId, media)`. The taste signal exists in the user's own `Review` (rating 1–10) + `Watchlist`
entries + the `Genre`/`MovieGenre` map (`IAppDbContext`). TMDB exposes recommendation/discovery endpoints that
return **real** titles.

Constraints: the LLM boundary of ADR 0016 (the engine speaks Cinora types only); the TMDB boundary of ADR 0006
(no DTO past the adapter); token/cost/latency caps (Phase-5 design §8); privacy/PII hygiene (§12).

## Decision

**Cinora generates the candidate universe from real TMDB data grounded in the user's own taste; the LLM only
ranks and explains that candidate set; and the handler enforces a hallucination guard that drops any pick not in
the offered set. The candidate set is the resolution table.**

### 1. Candidate generation is Cinora's, from real TMDB titles (Application)

- `IUserTasteProfileBuilder` (Application, `IAppDbContext`) projects a **compact** `TasteProfile` for a target
  `userId`: the user's **top-rated** reviewed titles (title/year/rating/genres), up to ~10 **watchlist** titles,
  the top ~5 **favorite genres**, and a **seen-exclusion set** of `(TmdbId, Media)` the user has reviewed or
  watchlisted. Bounded on purpose (input-size + PII control).
- `IRecommendationCandidateSource` (Application, `ITmdbClient`) fans out **real, resolvable TMDB titles** from
  the profile: "more like your highest-rated" via TMDB `/{media}/{id}/recommendations`
  (`GetRecommendationsAsync`), "in your favorite genres" via `/discover/{media}?with_genres=`
  (`DiscoverByGenreAsync`), and a popular/top-rated top-up for a thin profile. It then **dedups by
  `(TmdbId, Media)`, removes the seen-exclusion set, and caps to `MaxCandidates` (≈40)**. It returns Application
  `CandidateTitle` records only (no TMDB DTO — ADR 0006).
- Two **new `ITmdbClient` methods** (`GetRecommendationsAsync`, `DiscoverByGenreAsync`) extend the existing port
  + `TmdbClient` adapter + `CachedTmdbClient` decorator (new cache keys/TTLs; never cache null; degrade — the
  ADR-0007 rules unchanged). This is the only external-surface growth of Phase 5.

### 2. The LLM ranks + explains only the candidate set (Infrastructure via ADR 0016)

- The prompt gives the model the compact profile and a **numbered candidate list** (`id`=TmdbId, title, year,
  genres) and instructs it to pick the best matches **from the candidates only**, use each candidate's `id`, and
  return strict JSON `{ "picks": [ { "id": <candidate id>, "reason": "..." } ] }`. The candidate list is the
  model's entire allowed output vocabulary; every legitimate pick resolves by construction.

### 3. The hallucination guard is authoritative (Application handler)

- Independent of the prompt, `GenerateRecommendationsCommand` **keeps a pick only if its `(TmdbId, Media)` is in
  the offered candidate lookup**, drops anything else (with a Warning count), dedups, and caps to `MaxResults`
  (≈12). Card metadata (title/poster/year/genres) comes from the **candidate** (from TMDB), never from the
  model's free text — only the `Reason` string is taken from the model. A run that yields **zero** valid picks
  is treated as a generation failure → fall through to the deterministic heuristic on serve (ADR 0018).

## Consequences

**Positive**
- **Every recommendation resolves** — grounding + guard make a hallucinated or unresolvable title impossible to
  serve. No fuzzy title matching is ever needed (picks reference the exact `(TmdbId, Media)` offered).
- **Genuinely personal + explainable** — candidates derive from the user's own top-rated/genres/watchlist, and
  each pick carries a model-written one-line reason ("why this").
- **The engine stays pure and cheap** — it ranks a bounded set (≈40 in, ≈12 out) rather than searching all of
  cinema; token cost is bounded (design §8) and the same caps make a free-tier cloud swap viable.
- **The candidate source is fake-`ITmdbClient`-testable**; the guard is a pure set-membership unit test (no
  model needed).

**Negative / accepted costs**
- **Recommendations are bounded by the candidate universe** — a great pick outside the TMDB
  recommendations/discover fan-out is never surfaced. Accepted: the fan-out sources (recommendations + genre
  discover + popular top-up) give broad, taste-relevant coverage; widening the fan-out is a tuning knob.
- **Two new `ITmdbClient` methods + cache keys** — a small port/adapter/decorator growth, consistent with how
  the TMDB surface has grown per phase.
- **Candidate generation adds TMDB fan-out per user** on the (batch, off-request) precompute path — mitigated by
  the `CachedTmdbClient` decorator and the batch schedule (ADR 0018); it never runs on a page render.
- **A weak local model may rank sub-optimally** — but grounding means bad ranking degrades to a less-ideal
  *order of real titles*, never broken/unresolvable ones; a stronger free-tier model is a config swap (ADR 0016).

## Alternatives considered

1. **Free-text LLM titles, then resolve by name against the catalog.** The naive approach. **Rejected:**
   hallucination + fuzzy string resolution (ambiguous remakes, locale/aka titles, year collisions) — exactly the
   failure the exit criterion forbids. Grounding by candidate `id` eliminates both.
2. **Let the model return TMDB ids directly, ungrounded (no candidate list).** Simpler prompt. **Rejected:**
   models do not reliably know TMDB ids; it just moves hallucination to the id space. The offered candidate list
   is the only trustworthy id vocabulary.
3. **Pure heuristic, no LLM (popularity/genre/"more-like-your-top-rated").** Deterministic, zero model cost.
   **Rejected as the primary path** (loses the explanations + cross-genre nuance an LLM adds) but **kept as the
   outage/cold-start fallback** (ADR 0018) — the same candidate fan-out, minus the ranking step.
4. **Embeddings + vector similarity over the catalog.** Better recall, no prompt. **Deferred** (out of Phase 5
   scope, per the brief) — an embedding model + vector index is a larger build with no present need; the
   pipeline does not preclude adding it as a candidate source later.
5. **Persist every candidate as a `Movie` row before ranking (ADR-0008 `EnsureTitleCached` per candidate).**
   **Rejected as unnecessary:** serving renders from the candidate's TMDB metadata by `(tmdbId, media)`; the
   internal `Movie.Id` is only needed on a *write* (watchlist/review), which already runs its own
   `EnsureTitleCached` (ADR 0014). Persisting ≈40 candidates per user per night would bloat the catalog with
   titles no one opened — the same reason rails/search never persist (ADR 0007 §4.1). The candidate set in
   memory is the resolution table; no DB write is needed to ground a recommendation.

## Related
- ADR 0016 (the engine port the ranking runs through), ADR 0018 (caching/history + the heuristic that reuses the
  candidate fan-out as the fallback)
- ADR 0006/0007/0008 (TMDB boundary, caching, `(TmdbId, MediaType)` catalog identity), ADR 0014 (watchlist writes
  run `EnsureTitleCached` — why serving needs no internal `Movie.Id`)
- `docs/architecture/phase-5-ai-design.md` (§5 grounding, §5.4 guard, §9 heuristic)
- Code: `src/Cinora.Domain/Entities/{Review,Watchlist,Movie,Genre,MovieGenre}.cs`,
  `src/Cinora.Application/Common/Interfaces/{ITmdbClient,IAppDbContext}.cs`
- Skills: `.claude/skills/openai-recommendations/SKILL.md`, `.claude/skills/tmdb-api-integration/SKILL.md`,
  `.claude/skills/ef-core-data-access/SKILL.md`

---

_Design-only ADR (2026-07-03): no code written. Verified against Phase 1–4: `Review` (rating 1–10 via
`Rating`), `Watchlist` (`WatchlistStatus` PlanToWatch/Watching/Watched), `Movie`/`Genre`/`MovieGenre` +
`(TmdbId, MediaType)` / `TmdbGenreId` unique indexes; `ITmdbClient` returns Application read models via
`CachedTmdbClient`; no candidate/profile/recommendation type exists in `src/`._
