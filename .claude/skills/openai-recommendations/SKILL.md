---
name: openai-recommendations
description: Use when building or debugging AI movie recommendations in Cinora — the IRecommendationEngine port over a free local LLM (Ollama) via Microsoft.Extensions.AI, structured JSON output, prompt grounding, AIRecommendationHistory persistence, or model-outage fallbacks.
---

# AI Recommendations (IRecommendationEngine — free, local)

## Overview
Recommendations go through an `IRecommendationEngine` port. The **free default provider is Ollama** — free, local, native Windows install, exposing an **OpenAI-compatible endpoint at `http://localhost:11434/v1`** (models e.g. `llama3.2`, `phi3.5`, `qwen2.5`). Prefer **Microsoft.Extensions.AI** (`IChatClient`) as the provider-agnostic abstraction so Ollama, a free-tier cloud API (Gemini, Groq), or (hypothetically) OpenAI are interchangeable. **The paid OpenAI API is NOT used.** Recs are precomputed in batch (Hangfire), grounded in the user's own reviews/ratings/watchlist, returned as strict JSON, validated against the real catalog, and persisted to `AIRecommendationHistory` — the model is never called on a page view.

## Quick Reference
| Task | Approach |
|---|---|
| Abstraction | `IRecommendationEngine` port wrapping `IChatClient` from `Microsoft.Extensions.AI` |
| Free default provider | Ollama, OpenAI-compatible `http://localhost:11434/v1`, model `llama3.2`/`phi3.5`/`qwen2.5` |
| Endpoint + model | From options (never hardcoded) — swap Ollama/Gemini/Groq without touching handlers |
| Deterministic shape | Request JSON output; validate the shape before use |
| Grounding | Compact profile: top-rated reviews, watchlist titles, favorite genres |
| Validation | Resolve every returned title against the `Movie` catalog — discard hallucinations |
| Consistency | `Temperature = 0.4f` — variety without off-taste picks |
| Persistence | Save prompt + raw response to `AIRecommendationHistory` |
| Serving | Read latest history row / cache; never call the model inline |
| Outage | Serve last history row or genre-based popular titles |

## Pattern
```csharp
// Endpoint + model come from options, NOT hardcoded. FREE default = Ollama:
//   "Ai": { "Endpoint": "http://localhost:11434/v1", "Model": "llama3.2" }
// Ollama speaks the OpenAI-compatible API, so any IChatClient over it works; a free-tier
// cloud provider (Gemini/Groq) is a config swap. The paid OpenAI API is NOT used.
public sealed class OllamaRecommendationEngine(
    IChatClient chat, IUserTasteProfileBuilder profileBuilder,
    ICatalog catalog, IAiRecommendationHistoryRepository history)
    : IRecommendationEngine
{
    public async Task<IReadOnlyList<RecommendationDto>> GenerateAsync(
        Guid userId, CancellationToken ct)
    {
        // WHY grounding: without the user's real reviews/ratings/watchlist the model
        // returns generic top-100 lists; keep the profile compact to bound input size
        var profile = await profileBuilder.BuildAsync(userId, maxItems: 30, ct);

        var response = await chat.GetResponseAsync(
            [
                new(ChatRole.System,
                    "You are Cinora's movie recommender. Suggest films the user has NOT " +
                    "seen, matching their demonstrated taste. Respond only with JSON."),
                new(ChatRole.User, profile.ToPromptText())
            ],
            new ChatOptions { Temperature = 0.4f, ResponseFormat = ChatResponseFormat.Json },
            ct);

        var json = response.Text;
        // WHY persist inputs AND outputs: auditability, debugging bad recs, cache source
        await history.SaveAsync(userId, profile.ToPromptText(), json, ct);
        // WHY validate: models hallucinate — keep only titles that exist in the real catalog
        return await catalog.ResolveKnownTitlesAsync(Parse(json), ct);
    }
}
```

Register `IChatClient` in Infrastructure from options (base address + model), pointing at Ollama's local `/v1` endpoint by default (see `.claude/skills/dotnet-configuration-options/SKILL.md`). Local Ollama needs no API key; a free-tier cloud provider is a config change, not a code change.

## Batch, Cache, and Fallback
Run `RecommendationPrecomputeJob` nightly via Hangfire (`.claude/skills/hangfire-background-jobs/SKILL.md`) for recently active users only; skip users whose taste profile is unchanged. Cache the rendered list via `IDistributedCache` (`.claude/skills/redis-caching/SKILL.md`). When the model is unavailable, serve the newest `AIRecommendationHistory` row; with no history, fall back to popular titles in the user's top genres — the page must never 500 because of the AI.

## Common Mistakes
| Mistake | Fix |
|---|---|
| Calling a paid API / hardcoding `api.openai.com` | Use Ollama or a free provider via options; endpoint + model are configuration |
| Calling the model synchronously on page render | Precompute via Hangfire; serve the cached/history result |
| Trusting titles the model returns | Validate each against the `Movie` catalog; discard hallucinated titles |
| Free-text output parsing | Request JSON output and validate the shape before use |
| Recommending already-seen movies | Include reviewed/watchlisted titles as exclusions in the prompt |
| Discarding prompts/responses | Persist both to `AIRecommendationHistory` |
