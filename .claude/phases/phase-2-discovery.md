# Phase 2 — Discovery

## Goal
Users can find and explore movies and series: rich home page, fast search, and a premium movie-details experience powered by TMDB.

## Prerequisites
Phase 1 exit criteria verified (auth works, schema in place, asset pipeline running).

## In Scope
- TMDB typed HttpClient with resilience, API-key options, and Redis caching of responses.
- Import/sync strategy: persist movies/genres on first touch; nightly Hangfire refresh job for stale metadata.
- Home page: trending / popular / top-rated rails with poster imagery, skeleton loading states.
- Search: query TMDB + local catalog, debounced UI (Alpine), paginated results.
- Movie/series details page: backdrop hero with gradient overlay, metadata, cast summary, community rating placeholder, add-to-watchlist button stub (wired in Phase 4).
- Image handling via TMDB size variants (no full-size posters in lists).

## Out of Scope
Reviews, likes, comments, friends, watchlist persistence (stub UI only), AI recommendations.

## Agents
architecture-agent (integration design), backend-agent, database-agent (catalog persistence/indexes), frontend-agent, performance-agent (caching strategy), testing-agent, documentation-agent.

## Skills
tmdb-api-integration, redis-caching, hangfire-background-jobs, dotnet-performance, razor-views, alpine-htmx-interactivity, premium-ui-design, ui-animations, responsive-accessibility.

## Review Gates
/review-architecture, /review-code, /review-performance (TMDB caching + home page weight), /review-security (API key handling), /review-ui.

## Exit Criteria
- Home, Search, and Details render real TMDB-backed data with skeleton states and responsive layout.
- Repeat requests hit Redis (verifiable via logs/metrics), respecting TMDB rate limits.
- Nightly refresh job registered and runnable on demand via Hangfire dashboard.
- All tests pass; zero Critical/High review findings open.
