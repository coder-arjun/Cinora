# ADR 0004 — Free, Local-Only Infrastructure (No Paid Services, No Docker, No Subscriptions)

- **Status:** Accepted
- **Date:** 2026-07-02
- **Phase:** 1 (Foundation) — governs every phase
- **Deciders:** user (constraint owner), orchestrator (to ratify), all specialist agents (to honour)

## Context

The source specs (PRD/TRD/`Prompt.txt`) name several paid or Docker-based cloud services:
Azure SQL, Azure Cache for Redis, Azure Blob Storage, Azure SignalR, the paid OpenAI API, and
Azure App Service hosting. The user requires that **every component run at zero cost on the
local development machine**:

- **Free only** — no paid services and no genuinely-metered tiers; only free/open-source
  software or genuinely free tiers.
- **No Docker** — Docker is not installed and must not be required (this also rules out
  Testcontainers; see ADR 0002).
- **Local only** — no cloud accounts or subscriptions; everything runs on this machine.
  Deployment stays local/self-hosted and **plans-only** (never executed by agents).

Without recording this as a decision, downstream agents would keep reaching for the
spec-named paid/Docker services. The authoritative machine-verified constraint and its
substitution mapping already live in **`CLAUDE.md` → "Free-Only Infrastructure Constraint"**;
this ADR ratifies that table as an architecture decision and states the guiding rule.

## Decision

Ship **only free, no-Docker, local providers**, while **keeping the Clean Architecture
port/interface** for each concern so a paid provider *could* be swapped in later without
reworking callers. The abstraction stays; only the free adapter is built.

Substitutions (authoritative source: the `CLAUDE.md` "Free-Only Infrastructure Constraint"
table — reproduced here for traceability, that table governs on any discrepancy):

| Concern | Spec named (paid/Docker) | Free replacement shipped | Abstraction / port |
|---|---|---|---|
| SQL Server (dev) | Azure SQL / Docker SQL | **LocalDB** `(localdb)\MSSQLLocalDB` | EF Core `DbContext` (ADR 0002) |
| SQL Server (prod) | Azure SQL | **SQL Server Express** (free) | same |
| Cache | Azure Cache for Redis | **built-in in-memory `IDistributedCache`** (`AddDistributedMemoryCache`); optional **Memurai Developer** if a real Redis is wanted | `IDistributedCache` (`CacheOptions`) |
| Blob storage (dev) | Azure Blob | **Azurite** emulator (no Docker) | `IFileStorage` (`FileStorageOptions`) |
| Blob storage (prod) | Azure Blob | **local filesystem** provider under app data | `IFileStorage` (`FileStorageOptions`) |
| AI recommendations | paid OpenAI API | **Ollama** (free, local, OpenAI-compatible `/v1`) via **Microsoft.Extensions.AI**; free-tier cloud (Gemini/Groq) as an alternative | `IRecommendationEngine` (`AiOptions`) |
| Movie data | TMDB | **TMDB free API** (free non-commercial key) | typed `ITmdbClient` (`TmdbOptions`) |
| Auth | Google OAuth | **Google OAuth** — free credentials | ASP.NET Identity (`GoogleAuthOptions`) |
| Realtime | Azure SignalR | **self-hosted SignalR** (no backplane unless scaling) | hub |
| Background jobs | Hangfire | **Hangfire OSS** + SQL Server storage (free) | — |
| Push | — | **Web Push + self-generated VAPID keys** (free) | — |
| Hosting | Azure App Service | **local / free tier only**; deployment plans-only | — |

Notes tied to the current build:
- **Cache:** `CacheOptions.Provider` defaults to `"Memory"`; a Redis connection string is only
  read when a Redis-compatible provider is selected.
- **File storage:** `FileStorageOptions.Provider` defaults to `"Local"` (filesystem under
  `App_Data/uploads`, served at `/uploads`).
- **AI:** `AiOptions.Provider` defaults to `"Ollama"`, endpoint `http://localhost:11434/v1`,
  model `llama3.2`. This is a Phase 5 decision and revisitable behind `IRecommendationEngine`.
- The `IRecommendationEngine`, `IFileStorage`, `ITmdbClient` ports and the Redis/SignalR/Hangfire
  adapters are **Planned** for their respective later phases; Phase 1 ships the Options classes
  (`AiOptions`, `CacheOptions`, `FileStorageOptions`, `TmdbOptions`) and the persistence/auth
  providers only.

## Consequences

**Positive**
- Zero cost and zero cloud prerequisites; any developer can run Cinora with the .NET SDK,
  Node, and LocalDB already present on the machine.
- Every replaced concern sits behind a port, so a future move to a paid provider is an
  Infrastructure-only change — callers in Application/Web are unaffected.
- No Docker dependency anywhere (consistent with ADR 0002's LocalDB-over-Testcontainers choice).

**Negative / accepted costs**
- Free providers have lower ceilings: in-memory cache is per-process (no cross-instance
  sharing), self-hosted SignalR needs a backplane to scale out, LocalDB is Windows-only, and a
  local LLM depends on local hardware. These are acceptable for a local-first, single-instance
  build and are isolated behind their ports.
- Some spec-named capabilities (e.g. distributed cache coherence, managed blob durability) are
  deliberately not realised until — and unless — a paid provider is swapped in.
- Deployment is **plans-only**; no agent provisions or executes cloud infrastructure.

## Alternatives considered

1. **Use the spec-named paid Azure/OpenAI services directly.** Matches the specs verbatim but
   violates the free-only + local-only constraint (cost, cloud accounts). **Rejected.**
2. **Docker / Testcontainers for local parity (SQL, Redis, Azurite).** The team's usual choice
   for realistic local infra, but **Docker is not installed and is disallowed** (ADR 0002).
   **Rejected.**
3. **Free managed tiers on a cloud account (e.g. hosted free-tier DB/cache).** Still requires a
   subscription/cloud account and can meter or expire. **Rejected** in favour of on-machine tooling
   (free-tier cloud LLMs remain an explicitly-noted *alternative* to Ollama, revisited at Phase 5).
4. **Drop the ports and code straight against the free providers.** Less ceremony now, but would
   make a later paid swap a cross-layer rewrite. **Rejected** — the ports are cheap and preserve
   the Clean Architecture boundary (ADR 0001).

## Related
- `CLAUDE.md` → "Free-Only Infrastructure Constraint" (authoritative constraint + table)
- ADR 0001 (layering — the ports live in Application; adapters in Infrastructure)
- ADR 0002 (LocalDB / no Docker — the database substitution)
- ADR 0005 (hand-rolled mediator — a licensing-driven free substitution for MediatR)
- `docs/architecture/solution-structure.md` (§4 packages, §5/§6 Options, §7 policy)

---

_Last verified against code: 2026-07-02 — `src/Cinora.Infrastructure/Options/{AiOptions,CacheOptions,FileStorageOptions,TmdbOptions,GoogleAuthOptions}.cs` (defaults and section names as stated); ports `IRecommendationEngine`/`IFileStorage`/`ITmdbClient` are **Planned** (later phases)._
