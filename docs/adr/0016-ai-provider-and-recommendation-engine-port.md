# ADR 0016 — AI Provider (Ollama via Microsoft.Extensions.AI) behind the `IRecommendationEngine` Port

- **Status:** Accepted
- **Date:** 2026-07-03
- **Phase:** 5 (AI Recommendations), Milestone 5.0 (the LLM boundary)
- **Deciders:** architecture-agent (port/no-vendor-type boundary), ai-agent + backend-agent (to implement),
  security-agent (prompt data hygiene, consulted), orchestrator + user (ratify the free-provider default)

## Context

Phase 5 introduces the first **Large Language Model** integration in Cinora — a personal, explainable movie
recommender. Two questions must be settled once, together, because they define the whole AI boundary:

1. **Which provider?** The source specs (PRD/TRD/`Prompt.txt`) and the phase brief name the **paid OpenAI API**.
   That violates the free-only + local-only constraint (ADR 0004): a paid, metered, cloud service requiring an
   account and an API key. `CLAUDE.md`'s substitution table already names the replacement — **Ollama** (free,
   local, native Windows, no key, no rate limits, OpenAI-compatible `/v1` endpoint), preferring
   **Microsoft.Extensions.AI** as a provider-agnostic client, with free-tier cloud (Gemini/Groq) as an
   alternative. This ADR ratifies that as an architecture decision and pins the seam.

2. **How does the LLM cross the Clean Architecture boundary?** The vendor client (`IChatClient`,
   `ChatMessage`, `ChatOptions`, `OpenAIClient`) is an Infrastructure concern. Application handlers must depend
   only on an abstraction that speaks **Cinora types**, so the provider can change without touching a handler,
   and so the recommendation logic is unit-testable without a live model. `AiOptions`
   (`Provider="Ollama"`, `Endpoint="http://localhost:11434/v1"`, `Model="llama3.2"`, `TimeoutSeconds=60`,
   `MaxOutputTokens=1024`) already exists (Phase 1) and foreshadows an `IRecommendationEngine` port — but no
   port, adapter, or `IChatClient` registration exists in `src/` yet.

Constraints: free/local-only (ADR 0004) — no paid OpenAI, no Docker, no subscription; the dependency rule
(ADR 0001) — ports in Application, adapters in Infrastructure, no vendor DTO into Application/Domain; no
`IConfiguration` in services (bind `AiOptions`); the licensing discipline that banned MediatR/FluentAssertions
(only free MIT/Apache packages).

## Decision

**Recommendations go through a new `IRecommendationEngine` Application port; the sole shipped adapter is
`OllamaRecommendationEngine`, which consumes a `Microsoft.Extensions.AI` `IChatClient` pointed at Ollama's
local OpenAI-compatible `/v1` endpoint. The paid OpenAI API is never called.**

### 1. The port (Application) speaks only Cinora types

- `Cinora.Application/Common/Interfaces/IRecommendationEngine.cs`:
  `Task<RecommendationEngineResult> RankAsync(RecommendationRequest request, CancellationToken ct)`.
- The request/result models (`RecommendationRequest`, `TasteProfile`, `RatedTitle`, `CandidateTitle`,
  `RecommendationEngineResult`, `RankedPick`, `AiUsage`) are **Application-owned records** in
  `Cinora.Application/Common/Ai/`. The only Domain type they use is the `MediaType` enum (Application→Domain is
  allowed — it is a shared primitive, not an aggregate).
- **The engine only ranks + explains a supplied candidate set** (ADR 0017). It does not read the DB, call TMDB,
  resolve the catalog, or persist — those stay in Application (the `GenerateRecommendationsCommand` handler and
  its seams). This keeps the adapter pure and fake-`IChatClient`-testable, and keeps every vendor type inside
  Infrastructure.
- **No `IChatClient`, `ChatMessage`, `ChatOptions`, `ChatResponse`, or `OpenAIClient` ever appears in
  `Cinora.Application` or `Cinora.Domain`.** They are internal to `OllamaRecommendationEngine`.

### 2. The adapter (Infrastructure) over Microsoft.Extensions.AI

- `Cinora.Infrastructure/Ai/OllamaRecommendationEngine.cs` builds the chat messages from the Application
  request, calls `IChatClient.GetResponseAsync` with `ChatOptions { Temperature = 0.3, ResponseFormat = Json,
  MaxOutputTokens = AiOptions.MaxOutputTokens }` under a **linked `CancellationTokenSource` timed out at
  `AiOptions.TimeoutSeconds`**, parses + **validates the JSON shape**, and returns `RecommendationEngineResult`
  with `AiUsage` from the model's reported `response.Usage`. On **transport failure or timeout** it throws a
  typed `RecommendationEngineException` so the caller can degrade (ADR 0018) — a page never 500s from the model.
  - **[AMENDED 2026-07-06 — ratified at the 5.0 architecture gate]** **Malformed/empty model output does NOT
    throw** — it **degrades in place to an empty pick set**. The write/serve path already treats zero valid
    picks as a generation failure (no `AIRecommendationHistory` row → heuristic fallback on serve — ADR 0018 /
    design §5.4), so an empty result and a throw converge on identical serve behavior; one degrade path is
    cleaner than two. `RecommendationEngineException` is therefore **reserved for the model *service* being
    unavailable (transport/timeout)**, sharpening the failure taxonomy. This matches the `CachedTmdbClient`
    "degrade, never crash" posture and **supersedes** the "parse failure throws" wording here and in design
    §4/§17 (which are retained as original intent).
- **Registration (`AddInfrastructure`):** `services.AddChatClient(...)` builds an OpenAI-compatible client from
  `AiOptions.Endpoint` + `AiOptions.Model` (a dummy API key satisfies the client; Ollama ignores it), then
  `services.AddScoped<IRecommendationEngine, OllamaRecommendationEngine>()`. **`AiOptions.ValidateOnStart()` is
  switched ON** (Phase 5 is the first consumer). Endpoint + model come from `AiOptions`, **never hardcoded**.
- **New free packages (Infrastructure only, pinned centrally):** `Microsoft.Extensions.AI`,
  `Microsoft.Extensions.AI.OpenAI` (+ its `OpenAI` dependency) — confirmed **MIT/free**, `net10.0`-targeting.
  The `OpenAI` client is pointed at **Ollama**, never `api.openai.com`.

### 3. Provider is a config swap, not a code change

Because the adapter talks the OpenAI-compatible protocol, a **free-tier cloud model** (Gemini/Groq) is selected
by changing `AiOptions.Endpoint`/`Model` (and supplying a real free key via user-secrets) — **no handler, port,
or adapter change**. A hypothetical future paid provider is likewise a registration-only swap. **Only the free
Ollama adapter ships.**

## Consequences

**Positive**
- **Zero cost, fully local, no key, no rate limits** by default (Ollama) — consistent with ADR 0004; nothing
  leaves the machine (a privacy win, §12 of the Phase-5 design).
- **The recommendation logic is provider-agnostic and unit-testable** — handlers fake `IRecommendationEngine`;
  the adapter fakes `IChatClient`. No live model in the test suite (mock-first, milestones 5.0–5.4).
- **The dependency rule holds** — vendor types never cross into Application/Domain; a provider change is an
  Infrastructure-only edit.
- **A stronger model is one config line away** (free-tier cloud) if local quality is insufficient — the port
  de-risks the provider choice.

**Negative / accepted costs**
- **Local-LLM quality/latency depends on local hardware and the pulled model** — a small model may rank loosely
  or occasionally break JSON. Mitigated by grounding (ADR 0017), the hallucination guard, JSON + shape
  validation, and the outage fallback (ADR 0018); and by the config-swap escape to a stronger free-tier model.
- **New third-party packages** — `Microsoft.Extensions.AI*` + `OpenAI`; each is confirmed MIT/free and pinned
  centrally (the licensing discipline that banned MediatR/FluentAssertions). One more dependency surface to
  track.
- **`Microsoft.Extensions.AI.OpenAI` API surface may still be evolving** — pin an exact version; the adapter is
  the single file affected by any breaking change.

## Alternatives considered

1. **The paid OpenAI API directly (per the spec/brief).** Matches the spec verbatim. **Rejected:** violates the
   free-only + local-only constraint (ADR 0004) — cost, metering, a cloud account, an API key.
2. **A provider-specific SDK (e.g. the Ollama .NET client) directly, no `Microsoft.Extensions.AI`.** Fewer
   packages. **Rejected:** ties the adapter to one vendor's API shape; `Microsoft.Extensions.AI`'s `IChatClient`
   makes Ollama/Gemini/Groq interchangeable for the price of one thin abstraction (the same reasoning that made
   `AddStandardResilienceHandler` preferable to hand-rolled Polly in ADR 0006).
3. **Inject `IChatClient` straight into the Application handler (no `IRecommendationEngine`).** One fewer type.
   **Rejected:** `IChatClient` is a vendor abstraction — injecting it into Application leaks the AI vocabulary
   (`ChatMessage`/`ChatOptions`) across the boundary and couples handlers to prompt mechanics. The narrow
   `IRecommendationEngine` port keeps Application speaking Cinora types.
4. **Local embeddings + a vector store (semantic similarity) instead of / alongside an LLM.** Potentially better
   recall. **Deferred** (out of Phase 5 scope per the phase brief — "embeddings/vector search recorded as a
   potential improvement"); it is a larger build (an embedding model, a vector index, ANN search) with no
   present need. The port does not preclude it later.

## Related
- ADR 0004 (free/local-only — the AI row: Ollama via Microsoft.Extensions.AI behind `IRecommendationEngine`)
- ADR 0001 (layering — port in Application, adapter in Infrastructure), ADR 0006 (the external-boundary pattern
  this mirrors for TMDB), ADR 0005 (hand-rolled mediator — the `ISender` the generation command runs through)
- ADR 0017 (grounded candidate-generate-then-rank — what the engine actually does), ADR 0018 (caching/history/
  fallback — how a failed engine call degrades)
- `docs/architecture/phase-5-ai-design.md` (§3 port, §4 adapter, §12 privacy)
- Code: `src/Cinora.Infrastructure/Options/AiOptions.cs` (`Provider`/`Endpoint`/`Model`/`TimeoutSeconds`/
  `MaxOutputTokens`, `SectionName="Ai"`); `src/Cinora.Application/Common/Interfaces/IAppDbContext.cs`
- Skills: `.claude/skills/openai-recommendations/SKILL.md`, `.claude/skills/dotnet-configuration-options/SKILL.md`,
  `.claude/skills/clean-architecture-dotnet/SKILL.md`, `.claude/skills/error-handling-logging/SKILL.md`

---

_Design-only ADR (2026-07-03): no code written. Verified against Phase 1–4: `AiOptions` defaults + section name
as stated (not yet `ValidateOnStart`); no `IRecommendationEngine`/`IChatClient`/`Microsoft.Extensions.AI` type
exists in `src/` (only the `AiOptions` XML-doc comments foreshadow the port); `ITmdbClient` +
`CachedTmdbClient` + `IAppDbContext` + `ISender` pipeline present as described._
