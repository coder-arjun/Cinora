# ADR 0006 — TMDB Integration Boundary: Port Returns Application Read Models; DTOs Stay in Infrastructure

- **Status:** Accepted
- **Date:** 2026-07-02
- **Phase:** 2 (Discovery), Milestone 2.0
- **Deciders:** architecture-agent (boundary), backend-agent (to implement), orchestrator (to ratify)

## Context

Phase 2 introduces Cinora's first external adapter: TMDB (The Movie Database, free tier) supplies
trending/popular/top-rated lists, search, and title details with credits. TMDB returns snake_case JSON
with its own field names and shapes. Three layers could plausibly "own" the returned type:

- **Domain** already has `Movie.FromTmdb(...)` and `Genre.Create(...)`, so the adapter *could* map TMDB
  JSON straight to Domain entities.
- **Infrastructure** owns the deserialization DTOs.
- **Application** defines the port `ITmdbClient` that handlers call.

The governing rules are firm: the dependency rule points inward (ADR 0001); **no external-API DTO ever
crosses into `Cinora.Domain`** (solution-structure.md §5). The open question is what concrete type
`ITmdbClient` returns, and where TMDB's DTOs are allowed to live. This decision also sets the template
for the next external adapter (Phase 5 AI / Ollama), so it is worth recording once.

A secondary decision: TMDB is a flaky third-party API (rate limits, 429s, transient blips). The design
needs a resilience strategy that is free (ADR 0004) and not hand-rolled.

## Decision

**`ITmdbClient` (Application) returns Application-owned read models. TMDB DTOs are `internal` to
Infrastructure and never leave it. The Domain entities are touched only on the persistence path, never
on the read path.**

Concretely, a strict three-hop mapping keeps each type in exactly one layer:

```
TMDB JSON
  → TmdbMovieDto / TmdbSearchDto / TmdbCreditsDto  (Cinora.Infrastructure/Tmdb/Dtos — internal,
                                                    snake_case [JsonPropertyName]; never public)
  → TmdbTitleSummary / TmdbTitleDetails /          (Cinora.Application/Common/Tmdb — the ITmdbClient
    TmdbCastMember / TmdbGenre / TmdbPage<T>         contract; Cinora-owned records; may use Domain MediaType)
  → Movie.FromTmdb(...) / Genre.Create(...)         (Cinora.Domain — persistence path only; see ADR 0008)
```

- **Port in Application, adapter in Infrastructure.** `ITmdbClient` is defined in
  `Cinora.Application/Common/Interfaces`; `TmdbClient` implements it in `Cinora.Infrastructure/Tmdb`.
  Handlers depend only on the port (testable with a fake — no HTTP).
- **Read models are the contract, not Domain entities.** Display rails/search must not mint or persist
  `Movie` aggregates (each `FromTmdb` call allocates a new `Guid` + `CachedAtUtc`), and the read models
  carry only what the UI needs (title, overview, dates, raw image paths, vote average, genre ids/names,
  cast). The read models may reference the Domain **`MediaType`** enum (Application→Domain is allowed),
  avoiding a duplicate enum.
- **DTOs are `internal`.** They deserialize TMDB's snake_case with `[JsonPropertyName]` and are mapped to
  read models by an Infrastructure `TmdbMapper`. They are never exposed on any public signature, never
  returned to Application/Web, and never reach Domain.
- **Domain is untouched on reads.** `Movie.FromTmdb` / `Genre.Create` are invoked only by the persistence
  seam — the `SyncGenresCommand` / `EnsureTitleCachedCommand` handlers (ADR 0008) — mapping a read model →
  Domain entity when we deliberately persist.
- **Resilience via `Microsoft.Extensions.Http.Resilience`.** Register
  `AddHttpClient<TmdbClient>(...).AddStandardResilienceHandler()` — a **free (MIT), first-party** handler
  (Polly v8 under the hood) that bundles rate limiter, total timeout, retry with backoff + jitter honoring
  `Retry-After` on 429, circuit breaker, and per-attempt timeout in one call. Chosen over hand-wiring raw
  Polly policies (reinvents a curated default) and over no resilience (transient TMDB blips would become
  user-facing 500s).
- **Auth via v4 Bearer token.** `TmdbOptions.ApiKey` is sent in the `Authorization: Bearer` header (out of
  URLs and logs); the user supplies a TMDB v4 Read Access Token in user-secrets. Because TMDB is now
  consumed, `TmdbOptions` is bound with **`ValidateOnStart()`** (fail fast on a missing key).

## Consequences

**Positive**
- **Domain purity is provably preserved** — no TMDB type can reach it; the read path never constructs
  Domain entities. Directly satisfies the "no external DTO in Domain" rule.
- **Handlers are trivially testable** against `ITmdbClient` with a fake; no `HttpClient`/network in
  Application tests.
- **One resilience call**, free and maintained, instead of owned Polly policy code.
- **Reusable template** for the Phase 5 AI adapter (`IRecommendationEngine` will follow the same
  port-returns-read-models / DTOs-stay-in-Infrastructure shape).

**Negative / accepted costs**
- **A dedicated read-model set** (5–6 records) plus a mapper is more types than returning DTOs or Domain
  entities directly — accepted as the price of the boundary. The read models are small and stable.
- **`ValidateOnStart` fails boot without a key** — intended fail-closed; mock-first milestones and the
  Testing environment use a dummy non-empty key + a faked `ITmdbClient` so the suite boots without a
  live credential.
- A new package (`Microsoft.Extensions.Http.Resilience`) enters the graph — free (MIT), first-party,
  pinned centrally.

## Alternatives considered

1. **Adapter maps TMDB JSON straight to Domain `Movie`/`Genre` and the port returns those.** Fewer types,
   and the skill's quick-reference leans this way. **Rejected:** it forces the read path to mint/persist
   Domain aggregates for titles that are only being displayed, conflates "TMDB search result" with "our
   cached catalog entity," and makes `MediaType`/vote/cast awkward to carry. The persistence path still
   maps to Domain — just not on reads.
2. **Port returns the TMDB DTOs directly.** Zero mapping. **Rejected:** snake_case, TMDB-shaped DTOs would
   leak into Application and views, coupling the whole app to TMDB's wire format and violating the DTO
   boundary the moment TMDB changes a field.
3. **Hand-rolled Polly v8 pipeline.** Full control. **Rejected** as reinventing the curated standard
   handler; revisitable if a bespoke policy is ever needed.
4. **Application read models + standard resilience handler (chosen).** Clean layer boundaries, testable
   handlers, free maintained resilience; costs a small read-model set and one package.

## Related
- ADR 0001 (layering — ports in Application, adapters in Infrastructure; DTOs never in Domain)
- ADR 0004 (free-only — free resilience handler, TMDB free tier)
- ADR 0007 (TMDB caching & catalog persistence — the decorator over this client, and the Domain
  persistence path)
- ADR 0008 (TMDB catalog persistence realized as two `ISender` commands, not an `ITitleCatalog` port)
- `docs/architecture/phase-2-discovery-design.md` (§2 integration, §9 milestone 2.0)
- Skills: `.claude/skills/tmdb-api-integration/SKILL.md`, `.claude/skills/dotnet-configuration-options/SKILL.md`
- Code (Phase 1, consumed here): `src/Cinora.Infrastructure/Options/TmdbOptions.cs`,
  `src/Cinora.Domain/Entities/{Movie,Genre}.cs`, `src/Cinora.Application/Common/Interfaces/IAppDbContext.cs`

## Amendments

- **2026-07-02 (Phase-2 exit-layer reconciliation):** this ADR's incidental cross-references to the
  downstream persistence seam originally routed it through a prospective `ITitleCatalog` Infrastructure
  port. That port was never built; the seam shipped as two `ISender` commands —
  `SyncGenresCommand` and `EnsureTitleCachedCommand`, whose handlers use `IAppDbContext` directly — and
  the port is deferred. **ADR 0008** records that decision. The Decision's data-flow diagram and the
  "Domain is untouched on reads" bullet were reconciled to name the commands (and ADR 0008) instead of
  the port. **This ADR's own decision is unchanged:** `ITmdbClient` still returns Application read models,
  TMDB DTOs still stay `internal` to Infrastructure, and Domain is still touched only on the persistence
  path.

---

_Design-only ADR (2026-07-02): no code written. Verified against Phase 1: `TmdbOptions.ApiKey/BaseUrl/
ImageBaseUrl` exist and are bound lazily (ValidateOnStart to be switched on in 2.0); `Movie.FromTmdb`
and `Genre.Create` factory shapes; `IAppDbContext.Movies/Genres/MovieGenres`._
