# Phase 5 — AI Recommendations

## Goal
Personal, explainable AI recommendations grounded in each user's actual taste — precomputed, cached, and cheap to serve.

## Prerequisites
Phase 4 exit criteria verified (reviews, ratings, and watchlists provide the taste signal).

## In Scope
- OpenAI integration (options-configured, resilient, budget-capped): structured JSON recommendations from the user's reviews, ratings, and watchlist.
- Candidate grounding: recommendations must map to real catalog/TMDB titles (validate model output against the catalog; discard hallucinated titles).
- AIRecommendationHistory: persist prompt inputs summary, outputs, model, token usage per run.
- Hangfire precompute: recommendations refreshed on schedule and after significant taste events (new ratings), never computed synchronously on page view.
- "For You" rail on Home + dedicated recommendations page with "why this" explanations; feedback controls (dismiss / not interested) feeding the next run.
- Redis caching of the served recommendation set; graceful fallback (popular titles) when AI output is unavailable.

## Out of Scope
Embeddings/vector search (record as potential improvement), chat-style discovery.

## Agents
architecture-agent (pipeline design first), ai-agent, backend-agent, database-agent (history schema), performance-agent (cost/latency), testing-agent (deterministic tests via faked AI client), ux-agent, documentation-agent.

## Skills
openai-recommendations, hangfire-background-jobs, redis-caching, dotnet-configuration-options, error-handling-logging, cqrs-mediatr, premium-ui-design, ui-animations.

## Review Gates
/review-architecture, /review-code, /review-security (prompt data hygiene — no PII beyond necessity), /review-performance (zero synchronous AI calls in requests), /review-ui.

## Exit Criteria
- Recommendations render from precomputed data only; page load makes no OpenAI calls.
- Every run recorded in AIRecommendationHistory with token usage; hallucinated titles filtered out.
- Fallback path verified with the AI client disabled.
- All tests pass (AI client faked); zero Critical/High findings open.
