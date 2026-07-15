# Cinora — Phase 5 AI Recommendations Solution Design (Authoritative)

- **Status:** Accepted for Phase 5 (AI Recommendations) — pre-implementation design review
- **Date:** 2026-07-03
- **Owner:** architecture-agent
- **Builds on:** [Phase 1 solution structure](solution-structure.md), [Phase 2 Discovery design](phase-2-discovery-design.md),
  [Phase 3 Reviews & Social design](phase-3-social-design.md),
  [Phase 4 Watchlists & Profile design](phase-4-watchlists-profile-design.md)
- **New ADRs:** [0016 AI provider & the `IRecommendationEngine` port](../adr/0016-ai-provider-and-recommendation-engine-port.md),
  [0017 Grounded candidate-generate-then-rank + hallucination guard](../adr/0017-grounded-candidate-generation-and-ranking.md),
  [0018 AI output caching, history persistence & outage fallback](../adr/0018-ai-caching-history-and-fallback.md)
- **Governing ADRs:** [0001 Layering](../adr/0001-clean-architecture-layering.md),
  [0004 Free/local-only](../adr/0004-free-local-only-infrastructure.md),
  [0005 Hand-rolled mediator](../adr/0005-hand-rolled-mediator.md),
  [0006 TMDB integration boundary](../adr/0006-tmdb-integration-boundary.md),
  [0007 TMDB caching & catalog persistence](../adr/0007-tmdb-caching-and-catalog-persistence.md) (Hangfire deferral is redeemed here),
  [0008 Catalog persistence via commands](../adr/0008-tmdb-persistence-via-commands.md),
  [0009 Resource ownership & the current-user seam](../adr/0009-resource-ownership-and-current-user-seam.md)

This document is the single source of truth for Phase 5 (AI Recommendations). It designs the first **LLM
integration** in Cinora — a personal, explainable recommender grounded in each user's own taste — behind an
`IRecommendationEngine` port, precomputed off the request path, cached, persisted for audit, and degraded to
a deterministic non-AI heuristic when the model is unavailable. Implementation agents follow it; deviations
require an ADR.

> **Design-only.** No code was written and no build/test was run producing this document. Every `Verify`
> command in §17 is an acceptance check the *implementing* agent must run and show output for.

> **Governing constraints (unchanged; restated because Phase 5 is the first with an LLM):**
> - **Free / local-only (ADR 0004).** The phase brief names **OpenAI (paid)**. That is a paid service and is
>   **replaced by the free, local `IRecommendationEngine` port over Ollama** (native Windows, no API key, no
>   rate limits, OpenAI-compatible `/v1`), consumed through **Microsoft.Extensions.AI**'s provider-agnostic
>   `IChatClient` (ADR 0016). A free-tier cloud model (Gemini/Groq) is a **config swap**, not a code change;
>   the port stays provider-shaped so a paid provider *could* be swapped later. **The paid OpenAI API is never
>   called. No Docker, no subscription, no cloud account.** Likewise the served-recommendation cache is the
>   **free in-memory `IDistributedCache`** (`AddDistributedMemoryCache()`), not Redis/Azure (ADR 0004/0007);
>   the background scheduler is **Hangfire OSS + SQL Server storage** (free, no Docker), redeeming the ADR-0007
>   deferral.
> - **Hand-rolled mediator (ADR 0005).** Every "`ISender`/mediator" reference is the in-house type in
>   `Cinora.Application/Common/Messaging`. **Never add MediatR. Never add FluentAssertions** (tests use
>   xUnit `Assert` + NSubstitute). Handler unit tests fake `IRecommendationEngine`; adapter tests fake
>   `IChatClient`.
> - **Fail-closed contracts (Phase 1/3/4).** Global fallback authZ (`[Authorize]` unless `[AllowAnonymous]`);
>   global `AutoValidateAntiforgeryToken` (HTMX writes carry the token via the `RequestVerificationToken`
>   header); strict CSP. Phase 5 honors these and — importantly — needs **no CSP widening** (§13): the
>   **browser never calls the LLM**; the server talks to Ollama at `localhost` server-side, and the For-You
>   surfaces are same-origin HTMX over TMDB imagery already allowed.
> - **No generic repository; no business rules in handlers; no external/vendor DTO into Domain; no
>   `IConfiguration` injected (bind Options); ownership/privacy enforced in handlers via `ICurrentUser`.**
>   All reaffirmed. **No `IChatClient`/OpenAI/vendor type ever crosses into Application or Domain** — the
>   `IRecommendationEngine` port speaks only Application read-models + Domain primitives (§3).

---

## 1. What Phase 5 adds and where every concern lives

Phase 5 adds the **first LLM boundary** (`IRecommendationEngine`), the **grounded candidate→rank pipeline**,
the **served-recommendation cache**, the **precompute job**, and the **For-You surfaces** — built on the
**existing Phase-1 `AIRecommendationHistory` entity** (it already exists: `Create(userId, model, inputSummary,
outputSummary, promptTokens, completionTokens)`; index `(UserId, GeneratedAtUtc)`; `Restrict` FK to `User`).
It adds **no new entity and no column for the core path** (§14). The layering and dependency rule are
unchanged and inviolable (ADR 0001).

| Concern | Layer / location | Notes |
|---|---|---|
| **`IRecommendationEngine` port** (`RankAsync(RecommendationRequest, ct) → RecommendationEngineResult`) | `Cinora.Application/Common/Interfaces/IRecommendationEngine.cs` | NEW. Speaks **only Application read-models + Domain `MediaType`**; no `IChatClient`, no vendor type. The LLM **ranks + explains** a supplied candidate set; it does not fetch or invent. ADR 0016. §3. |
| **Engine models** — `RecommendationRequest`, `TasteProfile`, `RatedTitle`, `CandidateTitle`, `RecommendationEngineResult`, `RankedPick`, `AiUsage` | `Cinora.Application/Common/Ai/` | NEW. Cinora-owned records; the port's contract shapes. May use Domain `MediaType` (Application→Domain allowed). §3. |
| **`OllamaRecommendationEngine` adapter** (`IChatClient` via Microsoft.Extensions.AI; strict JSON; low temperature; timeout + linked `CancellationToken`; token-usage capture) | `Cinora.Infrastructure/Ai/OllamaRecommendationEngine.cs` | NEW. Maps the Application request ↔ chat messages internally; parses/validates JSON; returns `RecommendationEngineResult`. Throws `RecommendationEngineException` on timeout/parse/transport error. ADR 0016. §4. |
| **`IChatClient` registration** (Microsoft.Extensions.AI over the OpenAI-compatible client, pointed at `AiOptions.Endpoint`/`Model`) | `Cinora.Infrastructure/DependencyInjection.cs` (`AddInfrastructure`) | NEW. Base address + model from `AiOptions`; dummy API key (Ollama ignores it); `AiOptions.ValidateOnStart()` **ON** this phase (first consumer). §4, §16. |
| **`IUserTasteProfileBuilder`** (`BuildAsync(userId, ct) → TasteProfile` + the seen-exclusion set) | `Cinora.Application/Features/Recommendations/IUserTasteProfileBuilder.cs` (+ impl) | NEW, Application-internal seam (interface + impl in Application; deps `IAppDbContext`). Projects top-rated reviews, Watched titles, genre affinity, and the exclusion set. §5. |
| **`IRecommendationCandidateSource`** (`GenerateAsync(TasteProfile, ct) → IReadOnlyList<CandidateTitle>`) | `Cinora.Application/Features/Recommendations/IRecommendationCandidateSource.cs` (+ impl) | NEW, Application-internal seam (deps `ITmdbClient`). Fans out real TMDB titles from the profile, excludes seen, caps the pool. **The candidate set is the resolution table** (ADR 0017). §5. |
| **`ITmdbClient` extension** — `GetRecommendationsAsync(media, tmdbId, ct)`, `DiscoverByGenreAsync(media, genreTmdbIds, ct)` | `Cinora.Application/Common/Interfaces/ITmdbClient.cs` (extend) + `Cinora.Infrastructure/Tmdb/{TmdbClient,CachedTmdbClient}.cs` | NEW methods on the existing port + adapter + cache decorator (new keys/TTLs). Grounds candidate generation in real, resolvable TMDB titles. ADR 0017. §5.3. |
| **`GenerateRecommendationsCommand(Guid UserId)`** (+ handler) | `Cinora.Application/Features/Recommendations/GenerateRecommendationsCommand.cs` | NEW. Orchestrates profile → candidates → `IRecommendationEngine.RankAsync` → **hallucination guard** → persist `AIRecommendationHistory` → warm the served cache. A **Command** (writes) — CQRS-clean. Deps: the two Application seams + `IRecommendationEngine` + `IAppDbContext` + `IServedRecommendationCache`. §2, §6. |
| **`GetMyRecommendationsQuery()`** (+ handler) | `Cinora.Application/Features/Recommendations/GetMyRecommendationsQuery.cs` | NEW. The **serve path**: cache → latest `AIRecommendationHistory` row → deterministic heuristic. **Never calls the LLM.** Deps: `IServedRecommendationCache` + `IAppDbContext` + `ICurrentUser` + (heuristic) `ITmdbClient`. §7. |
| **`IServedRecommendationCache` port** (`GetAsync(userId, ct)`, `SetAsync(userId, set, ct)`) + adapter | `Cinora.Application/Common/Interfaces/IServedRecommendationCache.cs`, `Cinora.Infrastructure/Ai/ServedRecommendationCache.cs` | NEW. Per-user served set over the free in-memory `IDistributedCache`; key/TTL/serialization + graceful-degradation in the adapter (never throws; a cache miss/outage falls through). ADR 0018. §7. |
| **`RecommendationSet` / `RecommendationPick`** (served view model) | `Cinora.Application/Features/Recommendations/RecommendationSet.cs` | NEW Application records: ordered picks (`TmdbId`, `Media`, `Title`, `PosterPath?`, `ReleaseYear?`, `Reason`) + `RecommendationSource {Ai, Heuristic}` + `GeneratedAtUtc`. No Domain entity, no vendor type. §7, §11. |
| **`RecommendationPrecomputeJob`** (Hangfire recurring; selects active users; dispatches `GenerateRecommendationsCommand` per user; skips unchanged taste) | `Cinora.Infrastructure/Jobs/RecommendationPrecomputeJob.cs` | NEW. The job is the **orchestrator** (it may inject `ISender`; ADR 0008 pattern) — no `ISender` inside a handler. Idempotent, re-runnable. §10, ADR 0018. |
| **Hangfire wiring** — `AddHangfire(UseSqlServerStorage)`, `AddHangfireServer()`, `MapHangfireDashboard("/jobs")` admin-gated, `RecurringJob.AddOrUpdate` | `Cinora.Web/Program.cs` (+ `AdminDashboardAuthorizationFilter`) | NEW. Free Hangfire OSS + the existing SQL Server DB (redeems ADR 0007's deferral). Dashboard **fail-closed** (admin only). §10. |
| **`RecommendationsController` `[Authorize]`** — For-You page + rail partial + dismiss | `Cinora.Web/Controllers/RecommendationsController.cs` | `GET /recommendations` (page), `GET /recommendations/rail` (the For-You rail partial for `/home` lazy-load), `POST /recommendations/{tmdbId:int}/dismiss` (feedback). All reads dispatch `GetMyRecommendationsQuery` — **no LLM on any request**. §11. |
| **For-You UI** — `_ForYouRail`, `Recommendations` page, `_RecommendationCard` (poster + "why this" + dismiss) | `Cinora.Web/Views/Recommendations/`, `Views/Shared/` | Reuses the `_TitleCard` grammar + the Phase-4 `_WatchlistControl`. "Why this" caption per card. Reduced-motion respected. §11. |

**Anti-patterns still banned (reaffirmed):** no generic `IRepository<T>`; no `ISender` inside a handler (the
**job** orchestrates, ADR 0008); no business rules in handlers (the `Rating` 1–10 invariant stays on the
value object; the history-row guards stay on `AIRecommendationHistory.Create`); no `IConfiguration` in
services (bind `AiOptions`); **no `IChatClient`/OpenAI DTO past the adapter into Application/Domain** (ADR
0016); **the LLM is never called on a page render** (§7 exit criterion); no PII in the prompt (§12).

---

## 2. The recommendation pipeline (the one coherent data flow)

Two paths, deliberately separated so **serving never touches the model** (the phase exit criterion):

**Write path — precompute (Hangfire job → `GenerateRecommendationsCommand`), never on a request:**

```
RecommendationPrecomputeJob (nightly / on taste event)
  └─ for each active user →  sender.Send(GenerateRecommendationsCommand(userId))
       1. IUserTasteProfileBuilder.BuildAsync(userId)         → TasteProfile + seenExclusions   (IAppDbContext)
       2. IRecommendationCandidateSource.GenerateAsync(profile) → IReadOnlyList<CandidateTitle>  (ITmdbClient, real TMDB titles, seen removed, capped)
       3. IRecommendationEngine.RankAsync(request)            → RankedPick[] + AiUsage           (Infrastructure: IChatClient → Ollama; JSON; low temp; timeout+CT)
       4. HALLUCINATION GUARD: keep only picks whose (TmdbId,Media) ∈ candidate set; dedup; cap to K
       5. build RecommendationSet (Source = Ai) from the kept picks + their candidate metadata
       6. persist AIRecommendationHistory.Create(userId, model, inputSummary, outputSummary(JSON of the set), promptTokens, completionTokens); SaveChangesAsync
       7. IServedRecommendationCache.SetAsync(userId, set)                                        (IDistributedCache, per-user, TTL)
     (engine failure at step 3 → log Warning, DO NOT persist a history row, leave prior cache/history intact — the serve path degrades)
```

**Read path — serve (`GetMyRecommendationsQuery`), on every For-You render, LLM-free:**

```
GET /recommendations  or  GET /recommendations/rail   (RecommendationsController, [Authorize])
  └─ sender.Send(GetMyRecommendationsQuery())          (userId = ICurrentUser.GetRequiredUserId())
       a. IServedRecommendationCache.GetAsync(userId)     → hit? return it                        (fast path)
       b. else latest AIRecommendationHistory row for the user → parse OutputSummary → RecommendationSet (Source = Ai)   (durable fallback)
       c. else deterministic heuristic (Source = Heuristic): "more like your highest-rated" / "popular in your genres" / global-popular   (ITmdbClient, cached; NO LLM)
```

- **Grounding is what makes the model trustworthy** (ADR 0017): the model only ever sees a candidate list of
  **real TMDB titles** built from the user's own taste, and only **ranks + explains** them. It cannot invent
  a title that resolves to nothing — anything it returns outside the offered set is dropped at step 4.
- **CQRS purity:** `GenerateRecommendationsCommand` is the only writer (it `SaveChangesAsync` the audit row and
  warms the cache); `GetMyRecommendationsQuery` never writes. The **job**, not a handler, loops and dispatches
  — no `ISender` inside a handler (ADR 0008).
- **The two stores are complementary** (mirrors ADR 0007's cache/DB split): the **`IDistributedCache`** served
  set is the low-latency serve store (per-process, reconstructable); the **`AIRecommendationHistory`** row is
  the durable audit + the fallback source when the cache is cold/evicted. The **heuristic** is the last resort
  so the page always renders.

---

## 3. `IRecommendationEngine` — the port (Application; no vendor types leak)

The port is the **entire** contract Application knows about the LLM. It is deliberately **narrow**: it takes a
taste profile + a candidate set and returns a ranked, explained subset with token usage. It does **not** read
the DB, call TMDB, resolve the catalog, or persist — those are Application concerns (§5, §6), which keeps the
adapter pure and unit-testable with a fake `IChatClient`, and keeps `IChatClient`/OpenAI types entirely inside
Infrastructure (ADR 0016).

```csharp
// Cinora.Application/Common/Interfaces/IRecommendationEngine.cs — sketch, not literal
public interface IRecommendationEngine
{
    /// Ranks and explains the supplied candidate titles against the user's taste profile.
    /// The implementation MUST only return picks drawn from request.Candidates (the caller enforces this
    /// as a hallucination guard regardless). Throws RecommendationEngineException on timeout/parse/transport
    /// failure so the caller can degrade. Honors the CancellationToken and the configured per-request timeout.
    Task<RecommendationEngineResult> RankAsync(RecommendationRequest request, CancellationToken ct);
}
```

```csharp
// Cinora.Application/Common/Ai/  — records; Application-owned; MediaType is the only Domain type used
public sealed record RecommendationRequest(TasteProfile Profile, IReadOnlyList<CandidateTitle> Candidates);

public sealed record TasteProfile(
    IReadOnlyList<RatedTitle> TopRated,       // the user's highest-rated reviewed titles (title, year, rating, genres)
    IReadOnlyList<string> WatchlistTitles,    // titles they planned/are watching (interest signal)
    IReadOnlyList<string> FavoriteGenres);    // most-frequent genres across their ratings/watchlist

public sealed record RatedTitle(string Title, int? Year, int Rating, IReadOnlyList<string> Genres);

public sealed record CandidateTitle(
    int TmdbId, MediaType Media, string Title, int? Year, IReadOnlyList<string> Genres);

public sealed record RecommendationEngineResult(IReadOnlyList<RankedPick> Picks, AiUsage Usage);

public sealed record RankedPick(int TmdbId, MediaType Media, string Reason); // Reason ≤ N chars (§8)

public sealed record AiUsage(string Model, int PromptTokens, int CompletionTokens);
```

- **No vendor types.** `IChatClient`, `ChatMessage`, `ChatOptions`, `ChatResponse`, `OpenAIClient` are **all
  internal to `OllamaRecommendationEngine`**. The port and its models reference only Cinora records + the
  Domain `MediaType` enum (Application→Domain is allowed; that is a shared primitive, not an aggregate).
- **Picks reference candidates by `(TmdbId, Media)`** — the exact identity the caller offered — so the
  hallucination guard (§5.4) is a set-membership check, and the served card metadata (title/poster/year) comes
  from the candidate, not from anything the model wrote.
- **Usage is first-class** so §8's token accounting and the `AIRecommendationHistory` row are populated from
  the model's own reported counts (`ChatResponse.Usage`), not estimated.

**Shipped realization (Milestone 5.1 — accepted refinements, both uphold ADR 0017; NOT ADR amendments):**
- **`CandidateTitle` carries a `PosterPath`** (`string? PosterPath`) beyond this sketch — required by ADR 0017
  §3 so the served recommendation card renders a poster straight from the grounded candidate, with **no
  serve-time TMDB call**. It is **explicitly NOT sent to the model** (it plays no part in ranking); the picked
  title's metadata always comes from the candidate, never the model's free text.
- **`IUserTasteProfileBuilder` returns a `UserTasteResult` composite**, wider than the §1/§5.1 sketch's
  "`TasteProfile` + seen-exclusion set": it carries `Profile` + `SeedKeys` (the top-rated titles'
  `(TmdbId, Media)`) + `FavoriteGenreTmdbIds` + `SeenExclusions` + `HasSignal` (cold-start flag). The extra TMDB
  seed/genre ids exist because the candidate source (`IRecommendationCandidateSource`, §5.3) depends only on
  `ITmdbClient` and needs those ids to fan out real TMDB titles — while the prompt-shaped `TasteProfile` itself
  still holds titles/genres/ratings only, no ids (§12).

---

## 4. Infrastructure adapter — `OllamaRecommendationEngine` over Microsoft.Extensions.AI (ADR 0016)

```csharp
// Cinora.Infrastructure/Ai/OllamaRecommendationEngine.cs — sketch, not literal
internal sealed class OllamaRecommendationEngine(
    IChatClient chat, IOptions<AiOptions> options, ILogger<OllamaRecommendationEngine> log)
    : IRecommendationEngine
{
    public async Task<RecommendationEngineResult> RankAsync(RecommendationRequest request, CancellationToken ct)
    {
        var o = options.Value;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));           // WHY: bound tail latency (§8)

        var messages = BuildMessages(request);                            // system + one compact user message (§5)
        try
        {
            var response = await chat.GetResponseAsync(messages, new ChatOptions
            {
                Temperature = 0.3f,                                       // low → repeatable, on-taste (§8)
                MaxOutputTokens = o.MaxOutputTokens,                      // hard output cap (§8)
                ResponseFormat = ChatResponseFormat.Json,                 // structured output; validate the shape
            }, cts.Token);

            var picks = ParseAndValidate(response.Text);                  // strict JSON → RankedPick[]; throw on bad shape
            var usage = new AiUsage(o.Model,
                (int)(response.Usage?.InputTokenCount  ?? 0),
                (int)(response.Usage?.OutputTokenCount ?? 0));
            return new RecommendationEngineResult(picks, usage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || cts.IsCancellationRequested)
        {
            log.LogWarning(ex, "AI ranking failed for model {Model}", o.Model);
            throw new RecommendationEngineException("The recommendation model was unavailable.", ex);
        }
    }
}
```

- **Registration (`AddInfrastructure`), endpoint + model from `AiOptions`, never hardcoded (ADR 0016):**

  ```csharp
  services.AddOptions<AiOptions>()
      .BindConfiguration(AiOptions.SectionName)
      .ValidateUsingDataAnnotations()
      .ValidateOnStart();                       // NEW in Phase 5 — the LLM is now consumed (first consumer)

  // Microsoft.Extensions.AI over the OpenAI-compatible client, pointed at Ollama's local /v1.
  // Ollama needs no key; a dummy string satisfies the client. A free-tier cloud model (Gemini/Groq)
  // is a config swap (Endpoint/Model), NOT a code change. The paid OpenAI API is NOT used.
  services.AddChatClient(sp =>
  {
      var o = sp.GetRequiredService<IOptions<AiOptions>>().Value;
      var openAi = new OpenAIClient(new ApiKeyCredential("ollama"),
          new OpenAIClientOptions { Endpoint = new Uri(o.Endpoint) });
      return openAi.GetChatClient(o.Model).AsIChatClient();
  });                                            // optionally .UseLogging()/.UseFunctionInvocation() — not needed here

  services.AddScoped<IRecommendationEngine, OllamaRecommendationEngine>();
  ```

- **New free packages (pin centrally in `Directory.Packages.props`, Infrastructure only):**
  `Microsoft.Extensions.AI` (the `IChatClient` abstraction + `AddChatClient`), `Microsoft.Extensions.AI.OpenAI`
  (the OpenAI-compatible adapter that also speaks to Ollama's `/v1`), and its `OpenAI` client dependency. **All
  MIT/free.** No MediatR-style commercial trap; confirm the pinned versions target `net10.0` (same discipline
  as the Phase-2 `Microsoft.Extensions.Http.Resilience` pin). The adapter is the **only** file that references
  these packages.
- **Determinism + safety:** `Temperature = 0.3` (repeatable, on-taste — slightly tighter than the skill's 0.4
  because grounding already supplies variety via the candidate pool); `ResponseFormat = Json`; the adapter
  **validates the parsed shape** before returning (unknown/extra fields ignored; a malformed body throws →
  caller degrades). A per-request **timeout** (`AiOptions.TimeoutSeconds`, linked `CancellationToken`) bounds
  tail latency even in a background job. `MaxOutputTokens = AiOptions.MaxOutputTokens` caps generation cost.
- **`RecommendationEngineException`** (new, `Cinora.Application/Common/Exceptions/`) is the single failure the
  caller catches to trigger the fallback. It is **never** surfaced to a user request (generation runs in the
  job; the serve path independently degrades — §7). If a future on-demand "refresh now" endpoint dispatches
  generation inline, `GlobalExceptionHandler` maps `RecommendationEngineException` → **503**/friendly retry,
  never a 500 stack trace (mirrors the Phase-3/4 exception arms) — flagged for that future endpoint only.

---

## 5. Grounding — candidate-generate-then-rank + the hallucination guard (ADR 0017)

**The core correctness decision.** A bare "recommend movies for this taste" prompt returns a generic top-100
list peppered with titles that do not exist or do not resolve. Cinora inverts it: **Cinora generates the
candidate universe from real TMDB data; the LLM only ranks and explains it.**

### 5.1 Taste profile (`IUserTasteProfileBuilder`, Application, `IAppDbContext`)

One `AsNoTracking().Select(...)` read set, scoped to the target `userId`, projecting a **compact** profile:

- **Top-rated titles:** the user's reviews joined to `Movies`, ordered by `Rating` desc then recency, **top
  ~10** → `RatedTitle(Title, Year, Rating, Genres)` (genres via the existing `MovieGenre`→`Genre` id→name map).
- **Watchlist interest:** up to ~10 `PlanToWatch`/`Watching` titles (a positive-intent signal).
- **Favorite genres:** the top ~5 genres by frequency across the user's rated + watchlisted titles.
- **Seen-exclusion set:** the set of `(TmdbId, Media)` the user has **reviewed** or has **any watchlist entry**
  for — returned alongside the profile; candidates in this set are removed (§5.3) so recommendations are always
  *new* titles.

Bounded on purpose (input-size + PII control, §8/§12). A **thin profile** (no reviews, no watchlist) is a
cold-start signal → the handler skips the LLM and serves the heuristic directly (§9).

### 5.2 The prompt (built in the adapter from the Application request, §4)

- **System message:** role + hard rules — *"You are Cinora's recommender. From the numbered CANDIDATES only,
  pick the best matches for the user's taste and explain each in one short sentence. Never invent a title. Use
  each candidate's `id`. Respond only with JSON: `{ "picks": [ { "id": <candidate id>, "reason": "..." } ] }`."*
- **User message:** the compact `TasteProfile` (top-rated with ratings + genres, watchlist titles, favorite
  genres) followed by the **numbered candidate list** (`id`=TmdbId, title, year, genres). Titles + genres only
  — **no user id, no display name, no email** (§12).
- Grounding = the candidate list is the model's entire allowed output vocabulary. Because each candidate is a
  real TMDB title with a `TmdbId`, **every legitimate pick resolves** by construction.

### 5.3 Candidate generation (`IRecommendationCandidateSource`, Application, `ITmdbClient`)

Fan out **real, resolvable TMDB titles** from the profile, then dedup / exclude-seen / cap:

1. **"More like your highest-rated":** for each of the user's top ~5 rated titles →
   `ITmdbClient.GetRecommendationsAsync(media, tmdbId)` (TMDB `/{media}/{id}/recommendations`).
2. **"In your favorite genres":** `ITmdbClient.DiscoverByGenreAsync(media, favoriteGenreTmdbIds)` (TMDB
   `/discover/{media}?with_genres=`), sorted by popularity.
3. **Cold-start / thin fan-out top-up:** existing `GetPopularAsync`/`GetTopRatedAsync` fill any shortfall.
4. **Merge → dedup by `(TmdbId, Media)` → remove the seen-exclusion set → cap to `MaxCandidates` (≈40).**

- **New `ITmdbClient` methods** (`GetRecommendationsAsync`, `DiscoverByGenreAsync`) extend the existing port +
  `TmdbClient` adapter + `CachedTmdbClient` decorator with new cache keys/TTLs
  (`cinora:tmdb:recs:{media}:{tmdbId}` ≈ 12 h; `cinora:tmdb:discover:{media}:{genreCsv}` ≈ 6 h; ±10% jitter;
  never cache null; degrade — the ADR-0007 rules unchanged). This is the **only** external-surface growth of
  Phase 5 and keeps candidate generation grounded in TMDB rather than the sparse local `Movie` table.
- The candidate source returns **only Application `CandidateTitle` records** (no TMDB DTOs) — the ADR-0006
  boundary holds.

### 5.4 The hallucination guard (in `GenerateRecommendationsCommand`, authoritative)

Independent of the system prompt, the handler **enforces** grounding:

- Build a lookup of the offered candidates keyed by `(TmdbId, Media)`.
- For each `RankedPick` the engine returns: **keep it only if `(TmdbId, Media)` is in the candidate lookup**;
  drop anything else (a hallucinated or off-list id) with a Warning-level count. Dedup. **Cap to `MaxResults`
  (≈12).**
- The kept picks carry the model's `Reason`; the card metadata (title/poster/year/genres) comes from the
  **candidate** (which came from TMDB) — never from the model's free text. A model that returns *zero* valid
  picks (e.g. it hallucinated everything) is treated as a generation failure → no history row, fall through
  to the heuristic on serve. This is the exit-criterion guarantee: **hallucinated titles are filtered out.**

---

## 6. `AIRecommendationHistory` persistence (existing entity — no new store, ADR 0018)

The audit row is the **existing** `AIRecommendationHistory` (Phase 1). Its EF mapping already exists
(`AIRecommendationHistoryConfiguration`: PK `Id` value-generated-never; `Model` ≤ 200; `InputSummary`/
`OutputSummary` ≤ 4000; token ints; `GeneratedAtUtc`; index `(UserId, GeneratedAtUtc)`; `Restrict` FK to
`User`). Phase 5 **uses** it; it does **not** invent a parallel store.

- **Written once per successful generation** (step 6): `AIRecommendationHistory.Create(userId, model,
  inputSummary, outputSummary, promptTokens, completionTokens)` then `SaveChangesAsync`.
- **`InputSummary`** = a compact, bounded string of the grounding inputs (top-rated titles + favorite genres +
  candidate count) — for auditability/debugging, **≤ `SummaryMaxLength` (4000)**, PII-free.
- **`OutputSummary`** = a **compact JSON envelope of the served set**, deliberately sized to fit the 4000-char
  column: `{"v":1,"src":"ai","picks":[{"t":<tmdbId>,"m":0,"y":2014,"ti":"…","p":"/x.jpg","r":"short reason"}]}`
  with **`MaxResults` (≈12) picks** and each `Reason` capped (≈140 chars). This makes the row **self-contained**:
  the serve fallback (§7b) can render cards straight from it without a TMDB call. (If the budget is ever tight,
  the documented alternative is to store only `(t,m,r)` and re-hydrate title/poster from the details cache at
  serve — still LLM-free. Primary design stores the self-contained envelope.)
- **Token usage** (`PromptTokens`/`CompletionTokens`) comes from `AiUsage` (the model's reported counts) — the
  §8 cost/latency ledger and the review-gate's "every run recorded with token usage."
- **Dedup / skip-unchanged:** the precompute job skips a user whose taste is unchanged since their newest
  history row (compare a cheap **taste hash** — e.g. a hash over the ordered top-rated `(TmdbId,Rating)` +
  watchlist `(TmdbId,Status)` — stored *inside* `InputSummary`'s envelope, or the newest `GeneratedAtUtc` vs
  the user's latest review/watchlist `UpdatedAtUtc`). No duplicate row for an unchanged user (§10).
  - **Shipped realization (ADR 0018 amendment 2026-07-06):** the **second** option shipped — the job compares
    the newest `AIRecommendationHistory.GeneratedAtUtc` against the user's latest review/watchlist activity
    (within a 45-day lookback), **not** a stored taste hash. Timestamp-based skip is strictly more conservative
    (only ever over-regenerates, never serves a stale set after a taste change) and needs no schema change; the
    durable taste hash remains the recorded future refinement. See the ADR 0018 amendment.
- **"Why recommended"** is served from the row's per-pick `Reason` (or the cache's) — the explanation the UI
  shows (§11). The row is the source of truth for *why* a title was recommended and *what model/tokens* produced
  it.

---

## 7. Caching AI outputs + the serve path (ADR 0018)

### 7.1 `IServedRecommendationCache` (Application port, Infrastructure adapter over `IDistributedCache`)

- **Free provider only: `AddDistributedMemoryCache()`** (already registered in Phase 2; ADR 0004/0007). The
  `IDistributedCache` seam keeps a Redis-compatible swap a registration-only change later.
- **Per-user key** `cinora:ai:recs:{userId}`; **TTL ≈ 24 h** (aligned to the nightly precompute so a served set
  lives roughly one refresh cycle; ±10% jitter optional). Value = the serialized `RecommendationSet`.
- **Cache mechanics live in the adapter, not the handler** (SRP, exactly like `CachedTmdbClient`): serialization,
  key, TTL, and **graceful degradation** (every read/write in try/catch → `LogWarning` → a miss/no-op; a cache
  outage **never** surfaces as an error). The command warms it (step 7); the query reads it (step a).
- **Invalidation triggers:** (i) natural TTL expiry; (ii) **overwrite** on the next precompute (`SetAsync` after
  a successful `GenerateRecommendationsCommand`); (iii) **eviction on a taste event** — after a review create
  or a watchlist→`Watched`, the controller may `IServedRecommendationCache`-evict the user's key so the stale
  set is not served until the next precompute (optional, §10 event-driven refresh). A cold key simply falls to
  the history row (§7b), so eviction is safe and never 500s.

### 7.2 `GetMyRecommendationsQuery` — the LLM-free serve path (three tiers)

`userId = ICurrentUser.GetRequiredUserId()` (server-resolved; a user can only ever read **their own** set —
§12). In order:

- **(a) Cache hit** → return the `RecommendationSet` (`Source = Ai`). The steady-state fast path.
- **(b) Cache miss** → read the **latest `AIRecommendationHistory` row** for the user (index `(UserId,
  GeneratedAtUtc)` desc, `Take(1)`, `AsNoTracking`); parse `OutputSummary` → `RecommendationSet` (`Source =
  Ai`); opportunistically re-warm the cache is a *write*, so **not** done in the query — the next precompute
  re-warms it. Durable fallback with **no LLM call**.
- **(c) No history** (brand-new user, or precompute never ran / the model was down every run) → the
  **deterministic heuristic** (§9), `Source = Heuristic`. Always returns *something* so the page renders.

The query is **CQRS-pure** (no writes). It is the **only** thing the For-You surfaces call, and it **never**
invokes `IRecommendationEngine`/`IChatClient` — the hard exit criterion "page load makes no LLM calls."

---

## 8. Token / cost / latency controls (§ performance-agent + ai-agent)

Layered caps so a run is bounded and a page is instant:

| Control | Value / mechanism | Where |
|---|---|---|
| **Candidate cap** | `MaxCandidates` ≈ 40 (bounds the prompt's input tokens) | `IRecommendationCandidateSource` (§5.3) |
| **Profile cap** | top ~10 rated + ~10 watchlist + ~5 genres | `IUserTasteProfileBuilder` (§5.1) |
| **Output cap** | `AiOptions.MaxOutputTokens` (1024) + `MaxResults` ≈ 12 picks + `Reason` ≤ ~140 chars | adapter `ChatOptions` + guard (§4/§5.4) |
| **Per-request timeout** | `AiOptions.TimeoutSeconds` (60) via a linked `CancellationTokenSource` | adapter (§4) |
| **Determinism** | `Temperature = 0.3` | adapter (§4) |
| **Zero page-load model calls** | precompute-only; serve from cache/history/heuristic | §2/§7 |
| **Skip-unchanged** | **shipped:** timestamp — newest history `GeneratedAtUtc` vs latest activity, within a 45-day lookback (the taste hash was the design sketch — §6/§10, ADR 0018 amendment) | job (§10) |
| **Token ledger** | `PromptTokens`/`CompletionTokens` persisted per run | `AIRecommendationHistory` (§6) |
| **TMDB reuse** | candidate fan-out is cached by `CachedTmdbClient` | §5.3 |

- **Cost is CPU-time, not dollars** (Ollama is local/free) — but the same caps make a free-tier cloud swap
  (Gemini/Groq token quotas) viable without code change (ADR 0016).
- **Latency is off the request path** (batch job); the timeout protects the *job*, not a user request. The
  serve path is a cache/DB read (sub-millisecond to single-digit ms).

---

## 9. Model-outage & cold-start fallback — the deterministic heuristic (never 500, ADR 0018)

A non-AI recommender that **always produces a set** using only `ITmdbClient` (cached) + `IAppDbContext` — no
model, no vendor call:

- **"More like your highest-rated"** — for the user's top rated titles, `GetRecommendationsAsync` (the same
  candidate source, minus the ranking step), excluding seen, capped; **reason** is a template: *"Because you
  rated **{Title}** {Rating}/10."*
- **"Popular in your favorite genres"** — `DiscoverByGenreAsync` over the top genres, excluding seen; **reason**:
  *"Popular in **{Genre}**."*
- **Brand-new user (no signal at all)** — global `GetPopularAsync`/`GetTopRatedAsync`; **reason**: *"Popular on
  Cinora right now."*

Two consumers of the heuristic:

1. **Serve fallback (§7c)** — when neither cache nor history exists, `GetMyRecommendationsQuery` computes it
   live (`Source = Heuristic`) so the For-You surfaces render immediately for a new user.
2. **Generation fallback (optional)** — when the model is down, the precompute job MAY cache a **heuristic**
   set (`Source = Heuristic`) so the user still gets *something* labeled honestly, rather than nothing. It does
   **not** write an `AIRecommendationHistory` row (that store is the AI audit trail; a heuristic run has no
   model/tokens). Recommended: cache the heuristic set with a **shorter TTL** so the next healthy precompute
   replaces it with a real AI set promptly.

The `RecommendationSet.Source` flag lets the UI label a heuristic set subtly (e.g. "Popular picks" vs "For
You") and lets `/review-performance` verify **the page never 500s with the AI client disabled** (the exit
criterion, tested with a throwing fake `IChatClient` and with the whole engine unregistered).

---

## 10. Hangfire precompute (redeems the ADR-0007 deferral)

Phase 5 is the phase ADR 0007 named for standing up Hangfire ("Phase 5 AI needs a populated catalog / a
precompute driver"). **Free Hangfire OSS + SQL Server storage on the existing DB — no Docker, no paid queue.**

- **`RecommendationPrecomputeJob.RunAsync(ct)`** (Infrastructure) — recurring, **`Cron.Daily(4)` UTC**:
  1. select **active users** — those with a review or watchlist change since a lookback window (or since their
     latest history row); a `.Select(...)` over `Reviews`/`Watchlists`/`AIRecommendationHistories`.
  2. for each, **skip if taste unchanged** (design sketch: taste hash vs the newest history row — §6;
     **shipped realization:** timestamp-based — the newest `AIRecommendationHistory.GeneratedAtUtc` vs the user's
     latest review/watchlist activity within a 45-day lookback, not a hash; ADR 0018 amendment 2026-07-06);
  3. else `await sender.Send(new GenerateRecommendationsCommand(userId), ct)` — **the job is the orchestrator**
     (it may inject `ISender`; this is the ADR-0008 "controller/job orchestrates, handler stays pure" pattern —
     **not** `ISender` inside a handler).
- **Idempotent + safe to re-run** (Hangfire retries up to 10×): a re-run regenerates and overwrites the served
  cache + appends a history row; the `(UserId, GeneratedAtUtc)` index keeps history append-only and queryable.
  `[DisableConcurrentExecution]` on the recurring job guards overlap. Per-user failures are caught and logged so
  one bad user does not abort the batch.
- **Event-driven refresh (optional, second milestone):** after a review create or a watchlist→`Watched`, the
  **controller** (already the orchestrator) may enqueue a debounced per-user refresh via an
  `IRecommendationRefreshScheduler` **port** (Application) whose Infrastructure adapter calls
  `BackgroundJob.Schedule<GenerateRecommendationsJob>(...)` — so write handlers never reference Hangfire
  (dependency rule). Debounce (e.g. "at most one refresh per user per hour") avoids spamming the model on a
  rating spree. **Default ships nightly-only**; event-driven is a flagged refinement (§16).
- **Dashboard security (fail-closed):** `MapHangfireDashboard("/jobs", …)` gated by an
  `IDashboardAuthorizationFilter` that requires an **authenticated admin** (never anonymous, `Development`-only
  open at most — environment-name-exact per `CLAUDE.md`). Mirrors the ADR-0007/§2.6 note and the
  `security-hardening` rule. No job arguments beyond a `Guid userId`.
  - **Shipped realization (Milestone 5.3, ADR 0018 amendment 2026-07-06):** the filter is
    `AdminDashboardAuthorizationFilter` → the pure `AdminAccessPolicy`, gated by
    `AdminDashboardOptions.AdminAccounts` (config section `AdminDashboard`, `OrdinalIgnoreCase`). It matches an
    authenticated caller on their **server-assigned user-id (`ClaimTypes.NameIdentifier`) OR email claim** — the
    free-form **name-claim arm was dropped** (L1/L2 hardening) — and is **fail-closed** (an empty allow-list
    denies every caller, in every environment). A `Development`-only "any authenticated user" flag defaults off.

---

## 11. Where recommendations surface (UI/flows) + feedback

(frontend-agent + ux-agent; skills: `premium-ui-design`, `razor-views`, `alpine-htmx-interactivity`,
`ui-animations`, `responsive-accessibility`.)

- **"For You" rail on `/home`** (the authenticated feed, Phase 3) — a personalized rail above the friends feed.
  The page ships a **skeleton** and the rail **lazy-loads** via HTMX `GET /recommendations/rail`
  (`hx-trigger="load"`) — the same 2.3 rail grammar — so the feed never blocks on it. Each card reuses
  `_TitleCard` (poster + title + year) with a **"why this"** caption + the Phase-4 `_WatchlistControl` (add to
  watchlist inline). Empty/heuristic sets render honestly (a "Popular picks" heading), never an error.
- **Dedicated `/recommendations` page ("For You")** — the full `RecommendationSet` as a responsive grid, each
  card with its **"why this"** explanation and a **dismiss / not-interested** control. A subtle source label
  ("For You" for AI, "Popular picks" for heuristic). Owner-only (their own set).
- **`RecommendationsController` `[Authorize]`** (`ISender` + `ICurrentUser`):
  - `GET /recommendations` → the page (dispatches `GetMyRecommendationsQuery`).
  - `GET /recommendations/rail` → the `_ForYouRail` partial for the `/home` lazy-load (same query).
  - `POST /recommendations/{tmdbId:int}/dismiss` `{ media }` → dismiss feedback (below). Anti-forgery token via
    the `RequestVerificationToken` header; `[EnableRateLimiting(SocialWrite)]`.
  - **No action calls the LLM** — every read is `GetMyRecommendationsQuery` (cache/history/heuristic). This is
    the review-gate check for `/review-performance` ("zero synchronous AI calls in requests").
- **Feedback controls (dismiss / not-interested) — DECISION: ship immediate dismiss now; DEFER durable
  suppression (flagged).** The phase brief lists "dismiss / not interested feeding the next run," but **there
  is no schema field for per-title recommendation feedback** (`AIRecommendationHistory` is a per-run audit, not
  a per-title suppression list). Rather than silently invent a store:
  - **v1 (ships):** *dismiss* removes the card client-side (HTMX swap-out) for an instant response, and the
    candidate generator **already excludes** everything the user has reviewed or watchlisted — so acting on a
    recommendation (watchlist/review) naturally removes it from future runs. The exclusion-set plumbing (§5.1)
    is built to accept an **optional suppression source**, so a durable store slots in without reshaping the
    pipeline.
  - **DEFERRED (flagged to the user / a fast-follow, ADR-worthy):** *persistent "not interested"* that survives
    a reload and feeds the next run needs a small **`RecommendationFeedback`** entity (`UserId`, `TmdbId`,
    `MediaType`, `Kind = NotInterested`, `CreatedAtUtc`; unique `(UserId, TmdbId, MediaType)`) unioned into the
    §5.1 exclusion set. That is a **schema change** and is deliberately **not** taken in Phase 5's core to keep
    the phase schema-stable (the same YAGNI-on-schema posture as ADR 0015's deferred notification prefs and ADR
    0010's deferred chat). Recommended: defer; revisit if the product wants durable suppression. §16.
- **A11y:** the rail/page HTMX regions carry the established scoped focus-restore + polite status announce; the
  "why this" text is real, encoded copy (not `title`-only); dismiss is a keyboard-reachable button with an
  accessible name; reduced-motion respected.

---

## 12. Privacy & prompt data hygiene (the `/review-security` gate)

- **Only the acting user's own data** grounds their recommendations. The precompute takes an explicit `userId`;
  the serve path resolves `ICurrentUser.GetRequiredUserId()`. **No friend data, no other user's reviews/watchlist
  ever enters a profile or a candidate set** — a recommendation cannot leak who-rated-what. A user can only ever
  read **their own** `RecommendationSet` (owner-implicit, ADR 0009 — no id is bound on the serve path).
- **No PII in the prompt (data minimization).** The prompt carries **titles, years, genres, and integer
  ratings** only — **never** the user's id, display name, email, or any free-text profile field. The `userId`
  is used solely to *scope the DB query*; it is not sent to the model. Because the default provider (Ollama) is
  **local**, nothing leaves the machine at all — but the hygiene rule is enforced regardless so a free-tier
  cloud swap (Gemini/Groq, ADR 0016) inherits it.
- **`InputSummary`/`OutputSummary` are PII-free** (titles/genres/reasons only) — safe to persist and to show in
  the admin dashboard.
- **Recommendations are private to the user.** For-You surfaces are `[Authorize]` and owner-scoped; a
  recommendation set is never rendered on a public profile (respecting `IsProfilePublic` is moot here — the set
  is simply never exposed to another viewer).

---

## 13. Anti-forgery / authZ / CSP contract (all Phase-5 endpoints)

A single restated contract every new endpoint honors (the Phase-1/3/4 global contracts):

1. **Fail-closed authZ.** `RecommendationsController` is `[Authorize]`; the served set is owner-scoped via
   `ICurrentUser` (no bound id). There is **no `[AllowAnonymous]` Phase-5 endpoint** (recommendations are
   personal). The Hangfire dashboard is admin-gated (§10 — **shipped:** `AdminAccessPolicy` matches the
   server-assigned user-id or email, `OrdinalIgnoreCase`, fail-closed; the name-claim arm was dropped — ADR 0018
   amendment 2026-07-06).
2. **Anti-forgery.** The one state-changing endpoint (`POST /recommendations/{tmdbId}/dismiss`) carries the
   token via the already-wired `RequestVerificationToken` header — the global `AutoValidateAntiforgeryToken`
   validates it. The rail/page GETs are anti-forgery-exempt by construction.
3. **CSP — NO widening (load-bearing).** The **browser never contacts the LLM**; the server calls Ollama at
   `localhost:11434` **server-side**. The For-You rail/page are **same-origin** HTMX (`connect-src 'self'`
   already covers them), and cards render **TMDB posters** (`img-src 'self' https://image.tmdb.org data:`
   already in the CSP since Phase 2). **No directive is widened**, no new host, no `script-src` change (the
   dismiss/rail interactions use the existing HTMX + `@alpinejs/csp` name-only build). This is a deliberate,
   documented property — like Phase 3/4, Phase 5 adds a major capability without touching
   `SecurityHeadersMiddleware`.
4. **Rate limiting.** The dismiss POST carries `RateLimitingPolicies.SocialWrite` (per-user); the rail/page
   reads are cheap cache/DB reads and need no new policy. **Generation is off-request** (the job), so there is
   no per-request LLM rate surface to protect.

---

## 14. Migrations / schema deltas (for database-agent — do NOT write here)

**No new entity and no column change for the core path** — Phase 5 reuses the **existing**
`AIRecommendationHistory` entity + mapping (Phase 1). Confirm only:

| Milestone | Item | On | Why | Existing (unchanged) |
|---|---|---|---|---|
| 5.2 | **confirm** `IX_AIRecommendationHistories_UserId_GeneratedAtUtc` | `AIRecommendationHistory(UserId, GeneratedAtUtc)` | the "latest row per user" serve fallback (§7b) + the skip-unchanged check (§6) | present in `AIRecommendationHistoryConfiguration`; `Restrict` FK to `User` — **CONFIRMED** |
| 5.3 | Hangfire schema | its own tables | Hangfire OSS auto-creates its schema in the SQL Server DB on first run (`UseSqlServerStorage`) — **not** an EF migration | — |
| 5.0–5.4 | **none** | — | history mapping, indexes, and columns all exist; serving/caching/precompute add **no** table | — |

- **Config additions (not schema):** switch **`AiOptions.ValidateOnStart()` ON** (first consumer this phase);
  add the Hangfire storage connection string (reuse the existing `CinoraDb` connection). Options/wiring
  changes, not migrations.
- **New free packages** (Directory.Packages.props, §4/§10): `Microsoft.Extensions.AI`,
  `Microsoft.Extensions.AI.OpenAI` (+ its `OpenAI` dep), `Hangfire.AspNetCore`, `Hangfire.SqlServer` — all
  MIT/free, no Docker.

**Honest schema-insufficiency flags:**
1. **No per-title recommendation-feedback field** — durable "not interested" is **deferred** (§11, §16); v1
   ships client-side dismiss + seen-exclusion. A future `RecommendationFeedback` entity is the recorded shape.
2. **`OutputSummary` is bounded to 4000 chars** — the envelope caps to `MaxResults` (≈12) picks with capped
   reasons to fit (§6). If the product wants a larger served set, the re-hydrate-from-cache alternative (store
   `(t,m,r)` only, re-hydrate poster/title at serve) removes the pressure — flagged.
3. **No `Source`/heuristic marker column** — the AI-vs-heuristic distinction lives in the cache/`OutputSummary`
   envelope (`"src"`), not a DB column; the history table stays the **AI** audit only (a heuristic run writes
   no row). Accepted.

---

## 15. Performance stance

(performance-agent; skills: `dotnet-performance`, `redis-caching`, `hangfire-background-jobs`,
`ef-core-data-access`.)

- **Zero synchronous AI calls in requests** (the headline exit criterion) — every For-You render is a
  cache/DB/heuristic read via `GetMyRecommendationsQuery`; the model runs only in the Hangfire job. Verified by
  `/review-performance` and a test that renders the page with a **throwing** fake `IChatClient` (no 500, no
  call).
- **Serve latency:** (a) cache hit = one in-memory `IDistributedCache` read; (b) history fallback = one indexed
  `Top(1)` `AsNoTracking` read on `(UserId, GeneratedAtUtc)`; (c) heuristic = cached `ITmdbClient` fan-out.
  None touch the LLM.
- **Precompute is batched and bounded** — active-user selection + skip-unchanged keep the nightly run small; the
  candidate fan-out reuses the `CachedTmdbClient`; the per-request timeout + output cap bound each model call
  (§8). `[DisableConcurrentExecution]` prevents overlap.
- **`AsNoTracking().Select(...)` projections** for the profile/candidate/history reads; the history append is a
  single row. No N+1 (the profile's genre id→name is one map read; candidate metadata comes from TMDB, not
  per-card DB).
- **Async end-to-end**, `CancellationToken` threaded job → `ISender` → handler → engine/`IChatClient`/EF
  (no `.Result`, no sync-over-async). The engine's linked-token timeout is the tail-latency guard.
- **Cache is cheap + reconstructable** (free in-memory, ADR 0004): an eviction falls to the history row, a
  history gap falls to the heuristic — no path errors.

---

## 16. Key risks and product decisions

**Risks baked into the design:**
- **Local-LLM quality/latency depends on local hardware and the pulled model.** A small model (`llama3.2`,
  `phi3.5`, `qwen2.5`) may rank loosely or occasionally break JSON. Mitigated by: **grounding** (the model only
  ranks a real candidate set — bad ranking ≠ broken titles), the **hallucination guard** (off-list picks
  dropped), `ResponseFormat = Json` + **shape validation** (a bad body → generation failure → heuristic
  fallback, never a broken page), and **model/endpoint are config** (a stronger free-tier cloud model is a swap,
  ADR 0016).
- **Model outage / not-pulled.** Fully mitigated (§9): serve falls to history → heuristic; the page **never
  500s**. Verified with the engine disabled (exit criterion).
- **Stale recommendations between refreshes.** A user acts (rates/watchlists) and their For-You is a day old.
  Mitigated by seen-exclusion (acted-on titles vanish next run) + optional event-driven cache-eviction/refresh
  (§10). Accepted default: nightly freshness.
- **`OutputSummary` 4000-char budget** — capped picks/reasons fit (§6); the re-hydrate alternative removes the
  pressure if the set grows (§14).
- **Hangfire is new securable infra.** The dashboard is a real attack surface — **admin-gated, fail-closed**
  (§10). Job args are a bare `Guid`. Redeems ADR 0007's deferral rather than introducing it speculatively.
- **New AI packages** (`Microsoft.Extensions.AI*`, `OpenAI`) — confirm each is **MIT/free** and `net10.0`-target
  before pinning (the licensing discipline that banned MediatR/FluentAssertions). The `OpenAI` client is pointed
  at **Ollama**, never `api.openai.com`.

**Genuine PRODUCT / UX decisions (defer to the user or ux-agent — NOT architecture):**
- **Durable "not interested" feedback** — recommend **defer** (§11); ship client-side dismiss + seen-exclusion
  now; a `RecommendationFeedback` entity is the recorded future shape. **Flag to user.**
- **AI vs heuristic labeling** — how prominently to distinguish a real AI "For You" from a "Popular picks"
  fallback (a UX call; the `Source` flag supports either).
- **Refresh cadence** — nightly-only (default) vs event-driven per-user refresh after N ratings (§10) — a
  freshness/cost trade-off.
- **Movies + Series mix** — whether For-You blends media types or respects a user's dominant one; the pipeline
  supports either (candidates carry `Media`).
- **Rail placement** — For-You above the friends feed on `/home` vs a `/discover` slot for signed-in users — a
  UX/IA call; the rail partial works in either host.
- **Series recommendations parity** — the design treats movies + series symmetrically; whether Phase 5 ships
  both or movies-first is a scoping call.

---

## 17. Phase 5 milestone build order (delegable)

Ordered milestones for the orchestrator. Each states the owning agent(s), the deliverable, the exact `Verify`
command/acceptance, and the review gates (`/review-architecture`, `/review-code`, `/review-security`,
`/review-performance`, `/review-ui` — automatic after every milestone and at phase end). All commands run from
the repo root. Phase 5 is **mock-first** — 5.0–5.4 build and test against a **faked `IChatClient`** (adapter
tests) and a **faked `IRecommendationEngine`** (handler tests) plus the existing **faked `ITmdbClient`**, over
real **LocalDB `CinoraTest`** in integration tests. Only **5.5** needs a **local Ollama** with a pulled model
(free, no key) for the live smoke.

> **Dependency order:** 5.0 (engine port + adapter — the highest-risk boundary + prompt hygiene) → 5.1 (grounded
> candidates + rank + hallucination guard — the correctness core) → 5.2 (history persistence + serve cache +
> deterministic fallback — so serving works before any job) → 5.3 (Hangfire precompute) → 5.4 (For-You UI +
> feedback) → 5.5 (live verification with Ollama).

### 5.0 — `IRecommendationEngine` port + Ollama adapter (Microsoft.Extensions.AI) (mock-first)
- **Owner:** backend-agent + ai-agent + architecture-agent sign-off on the port/no-vendor-type boundary +
  security-agent (prompt data hygiene). ADR 0016.
- **Deliverable:** `IRecommendationEngine` + the engine models (`RecommendationRequest`/`TasteProfile`/
  `CandidateTitle`/`RecommendationEngineResult`/`RankedPick`/`AiUsage`) in `Cinora.Application`;
  `OllamaRecommendationEngine` + `RecommendationEngineException` in `Cinora.Infrastructure/Ai`; `IChatClient`
  registration from `AiOptions` (`AddChatClient` over the OpenAI-compatible client → Ollama `/v1`);
  `AiOptions.ValidateOnStart()` **ON**; the new AI packages pinned centrally. No UI, no DB.
- **Verify (mock-first):**
  ```
  dotnet build Cinora.sln -c Release
  dotnet test tests/Cinora.Infrastructure.Tests/Cinora.Infrastructure.Tests.csproj
  ```
  With a **fake `IChatClient`**: valid JSON → parsed `RankedPick[]` + `AiUsage` from `response.Usage`; a
  malformed body → `RecommendationEngineException` (not a crash); a slow client → the timeout cancels within
  `AiOptions.TimeoutSeconds`; the prompt contains **titles/genres/ratings only** (no user id/name/email —
  security assertion). App **boots** with a dummy endpoint; **no vendor type appears in `Cinora.Application`**
  (an architecture check). Build clean (TWAE on). Then `/review-architecture` (port boundary),
  `/review-security` (prompt hygiene), `/review-code`.

### 5.1 — Grounded candidate generation + rank + hallucination guard (mock-first)
- **Owner:** backend-agent + ai-agent + performance-agent (candidate caps). ADR 0017.
- **Deliverable:** `ITmdbClient` extension (`GetRecommendationsAsync`, `DiscoverByGenreAsync`) + `TmdbClient`
  adapter + `CachedTmdbClient` keys/TTLs; `IUserTasteProfileBuilder` + `IRecommendationCandidateSource` (+
  impls); `GenerateRecommendationsCommand` + handler (profile → candidates → engine → **guard** → build set)
  — persistence/cache wired in 5.2 (return the set for now).
- **Verify (mock-first, LocalDB):**
  ```
  dotnet test tests/Cinora.Application.Tests/Cinora.Application.Tests.csproj
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  With a **fake `IRecommendationEngine`** + **fake `ITmdbClient`**: the profile projects the user's own
  top-rated/watchlist/genres; candidates **exclude seen** titles and are **capped**; a pick the engine returns
  that is **not in the candidate set is dropped** (hallucination guard), duplicates collapse, the set caps to
  `MaxResults`; a thin profile → cold-start path. Then `/review-architecture`, `/review-code`,
  `/review-performance` (candidate caps).

### 5.2 — History persistence + served cache + deterministic fallback (mock-first)
- **Owner:** backend-agent + database-agent (confirm the history index) + performance-agent. ADR 0018.
- **Deliverable:** persist `AIRecommendationHistory` (bounded `InputSummary`/`OutputSummary` envelope + token
  usage) in `GenerateRecommendationsCommand`; `IServedRecommendationCache` port + `IDistributedCache` adapter
  (key/TTL/degrade); `GetMyRecommendationsQuery` (cache → history → heuristic); the deterministic heuristic
  (§9). Confirm `IX_AIRecommendationHistories_UserId_GeneratedAtUtc` (no migration expected).
- **Verify (mock-first, LocalDB):**
  ```
  dotnet test tests/Cinora.Application.Tests/Cinora.Application.Tests.csproj
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  A generation writes **one** history row with token usage + a parseable `OutputSummary`, and warms the cache;
  `GetMyRecommendationsQuery` returns the cached set (hit), else the latest history row (miss), else the
  **heuristic** (no history); with a **throwing/absent engine**, serve **never 500s** and yields a `Heuristic`
  set (the fallback exit criterion). Then `/review-performance` (no LLM on serve), `/review-code`.

### 5.3 — Hangfire precompute job + dashboard security (mock-first)
- **Owner:** backend-agent + security-agent (dashboard filter) + performance-agent. ADR 0018 (redeems ADR 0007).
- **Deliverable:** Hangfire OSS + `UseSqlServerStorage` on `CinoraDb`; `AddHangfireServer`; `MapHangfireDashboard
  ("/jobs")` admin-gated (`IDashboardAuthorizationFilter`); `RecommendationPrecomputeJob` (active-user select +
  skip-unchanged + per-user `ISender` dispatch, `[DisableConcurrentExecution]`); `RecurringJob.AddOrUpdate(…,
  Cron.Daily(4))`. (Optional event-driven refresh via `IRecommendationRefreshScheduler` — flagged.)
- **Verify (mock-first, LocalDB):**
  ```
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  With a **faked engine/TMDB**: the job is registered and **runnable on demand**; a run generates + caches for
  active users and **skips unchanged** ones; a re-run does not duplicate a served set (idempotent); the
  **`/jobs` dashboard 401/403s anonymously** (fail-closed). Then `/review-security` (dashboard auth),
  `/review-performance` (batch bounded), `/review-code`.

### 5.4 — For-You rail + `/recommendations` page + feedback (mock-first)
- **Owner:** backend-agent + frontend-agent + ux-agent + security-agent (anti-forgery/CSP).
- **Deliverable:** `RecommendationsController` (`/recommendations`, `/recommendations/rail`,
  `POST /recommendations/{tmdbId}/dismiss`); the For-You rail lazy-loaded on `/home`; the `/recommendations`
  page + `_RecommendationCard` (poster + "why this" + `_WatchlistControl` + dismiss); heuristic/empty labeling;
  nav link. **No LLM on any request.**
- **Verify (mock-first, LocalDB):**
  ```
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  npm run build --prefix src/Cinora.Web
  ```
  Authed `GET /recommendations` + `GET /recommendations/rail` → 200 with cards + **"why this"** text and
  **make no LLM call** (throwing fake `IChatClient` present, unused); anonymous → 302 login (fail-closed);
  dismiss requires the anti-forgery token; **CSP unchanged** (`script-src 'self'`, `connect-src 'self'`,
  `img-src …image.tmdb.org…` — no new host/directive). Then `/review-ui`, `/review-security` (CSP/anti-forgery),
  `/review-performance` (zero synchronous AI calls), `/review-code`.

### 5.5 — Live verification (NEEDS a local Ollama + a pulled model — free, no key)
- **Owner:** ai-agent + backend-agent (manual/gated).
- **Deliverable:** a running Ollama (`ollama serve`) with a pulled model (e.g. `ollama pull llama3.2`); a live
  run of the precompute job for a seeded user; the For-You surfaces rendering real, grounded, explained picks.
- **Verify (live):**
  ```
  # with Ollama running locally and a model pulled:
  dotnet run --project src/Cinora.Web/Cinora.Web.csproj
  #   trigger the precompute job once (admin /jobs dashboard "Trigger now", or an admin endpoint)
  #   GET /recommendations  → real grounded picks with "why this" explanations; every title resolves
  #   confirm AIRecommendationHistory has a new row with non-zero token counts
  #   stop Ollama, reload /recommendations → still renders (history/heuristic fallback, no 500)
  ```
  Recommendations render from **precomputed data**; the page makes **no** LLM call; every run is recorded with
  token usage; hallucinated titles are absent (grounding); the **fallback path works with the model stopped**.
  Then `/review-architecture` + the four other gates at phase end.

**End-of-phase gate (Exit Criteria):** `dotnet build Cinora.sln -c Release` clean (TWAE on); `dotnet test`
green across all four test projects (mock-first, LocalDB); **recommendations render from precomputed data only —
page load makes no LLM call**; **every run recorded in `AIRecommendationHistory` with token usage, hallucinated
titles filtered out**; **the fallback path is verified with the AI client disabled** (heuristic set, no 500);
**CSP unchanged**; the Hangfire `/jobs` dashboard is admin-gated; **zero Critical/High** across the five review
gates. Run the full review-gate loop at phase end.

---

_Design authored 2026-07-03 against the Phase-1–4 code (verified: `AIRecommendationHistory` entity —
`Create(userId, model, inputSummary, outputSummary, promptTokens, completionTokens)`, `ModelMaxLength=200`,
`SummaryMaxLength=4000`, and `AIRecommendationHistoryConfiguration` index `(UserId, GeneratedAtUtc)` + `Restrict`
FK to `User`; `AiOptions` — `Provider="Ollama"`, `Endpoint="http://localhost:11434/v1"`, `Model="llama3.2"`,
`TimeoutSeconds=60`, `MaxOutputTokens=1024`, `SectionName="Ai"`, not yet `ValidateOnStart`; `IAppDbContext`
exposing `AIRecommendationHistories`/`Reviews`/`Watchlists`/`Movies`/`Genres`/`MovieGenres` + `SaveChangesAsync`
+ `DiscardPendingChanges`; `ITmdbClient` port + `CachedTmdbClient` cache-aside decorator over the free in-memory
`IDistributedCache` (ADR 0006/0007); `EnsureTitleCachedCommand`/`GetCachedMovieIdQuery` (ADR 0008);
`ICurrentUser` seam (ADR 0009); `Review`/`Watchlist`/`Movie`/`Genre`/`User` entities + behaviors; the hand-rolled
`ISender` + Logging→Validation→Performance pipeline + `GlobalExceptionHandler`; fail-closed authZ +
`AutoValidateAntiforgeryToken` + `SecurityHeadersMiddleware` strict CSP; the `social-write`/`public-read`
rate-limit policies; **no `IRecommendationEngine`/`IChatClient`/Hangfire/`Microsoft.Extensions.AI` type exists in
`src/` — only the `AiOptions` comments foreshadow them**). No application code written._

_Shipped-realization reconciliation notes added 2026-07-06 (§3 record shapes; §6/§8/§10 timestamp-based
skip-unchanged; §10/§13 admin user-id-or-email gate) against the Phase-5 shipped code — `UserTasteResult` /
`CandidateTitle`, `RecommendationPrecomputeJob.SelectStaleUsersAsync`
(`RecommendationOptions.PrecomputeLookbackDays = 45`), `AdminAccessPolicy` / `AdminDashboardOptions`. See the ADR
0018 amendment (2026-07-06). No behavior changed. Last verified against code: 2026-07-06._
