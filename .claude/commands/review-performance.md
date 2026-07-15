---
description: Agentic loop — performance review until no Critical/High findings remain
---

# Performance Review Loop

Run an iterative performance review of Cinora until it is clean.

## Scope
EF Core query efficiency (N+1, missing projections/`AsNoTracking`, missing indexes for feed/search queries, offset pagination on large sets), caching (Redis cache-aside for TMDB responses and hot feeds, OutputCache on cacheable pages, cache stampede protection), async hygiene (blocking calls, sequential awaits that could be parallel), payload size (image sizing via TMDB size variants, response compression, JS/CSS bundle size), SignalR overuse, Hangfire jobs doing work that blocks requests, Core Web Vitals for key pages (Home, Movie Details, Feed).

## Loop Protocol
1. Dispatch **performance-agent** in review mode (no edits during review). Findings rated **Critical / High / Medium / Low** with file:line, the evidence (query plan, timing, payload size — measured where possible, not guessed), and a concrete fix.
2. Critical/High: performance-agent implements or delegates fixes to the owning specialist. Medium: fix if cheap, else `REVIEW_BACKLOG.md`. Low: record.
3. Verify: build + tests pass; re-measure the specific metric that was flagged to confirm improvement.
4. Re-run on affected areas. Exit at **zero Critical/High** or after **3 iterations** (report honestly what remains).

## Report
Findings table per iteration with before/after measurements where available, deferred items, final verdict.

## Hard Rules
- NEVER run `git commit` or `git push`.
- No optimization without evidence — measure first; do not "optimize" readable code on speculation.
