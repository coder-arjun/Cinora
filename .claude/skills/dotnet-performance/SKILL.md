---
name: dotnet-performance
description: Use when writing async code, optimizing EF Core queries, adding response caching, or diagnosing slow Cinora endpoints — sync-over-async suspects, N+1 queries in feeds, deep pagination, or latency regressions.
---

# .NET Performance

## Overview
Measure first, then optimize the proven hot path: async end to end, lean EF Core queries that fetch only what the view model needs, and caching for read-heavy pages like the feed and movie details.

## Quick Reference
| Task | Approach |
|---|---|
| Async | `async`/`await` all the way; never `.Result`, `.Wait()`, `Task.Run` in request paths |
| Cancellation | Accept `CancellationToken` in every handler; pass it to EF Core and HTTP calls |
| Hot allocations | `ValueTask<T>` only where completion is usually synchronous (cache hits) |
| Read queries | `Select` projection to DTOs + `AsNoTracking()` |
| Multiple includes | `AsSplitQuery()` to avoid cartesian explosion |
| Very hot lookups | `EF.CompileAsyncQuery` |
| Feed paging | Keyset (cursor) pagination — never `Skip`/`Take` at depth |
| Page caching | `OutputCache` with tags for invalidation; `ResponseCache` headers for CDN/browser |
| Before optimizing | BenchmarkDotNet or a profiler trace — no guessing |

## Pattern
```csharp
// Keyset pagination for the review feed: stable ordering, index-friendly,
// cost is O(page size) regardless of how far the user has scrolled.
public async Task<IReadOnlyList<FeedItemDto>> GetFeedAsync(
    DateTime? afterCreatedAt, long? afterId, CancellationToken ct) // WHY: token flows from ASP.NET Core so abandoned requests stop querying
{
    return await _db.Reviews
        .AsNoTracking() // WHY: read-only path — skip change-tracking overhead
        .Where(r => afterCreatedAt == null
            || r.CreatedAt < afterCreatedAt
            || (r.CreatedAt == afterCreatedAt && r.Id < afterId)) // WHY: Id tiebreaker keeps the cursor stable across equal timestamps
        .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
        .Select(r => new FeedItemDto(r.Id, r.Title, r.Rating,
            r.Author.DisplayName, r.Movie.PosterPath)) // WHY: projection fetches only needed columns and joins once — no N+1
        .Take(20)
        .ToListAsync(ct);
}
```

## Common Mistakes
| Mistake | Fix |
|---|---|
| `.Result` / `.GetAwaiter().GetResult()` in a handler | Make the whole chain async; sync-over-async starves the thread pool |
| Loading full entities, then mapping in memory | Project with `Select` inside the query |
| Lazy loading inside a view loop (N+1) | Project or eager-load; watch `Database.Command` logs in dev for repeated SQL |
| `Skip(page * size)` on the infinite feed | Keyset pagination on `(CreatedAt, Id)` |
| `ValueTask` everywhere by default | Only on measured hot paths; never await a `ValueTask` twice |
| Optimizing on a hunch | Benchmark or trace first, then change one thing |

For query shape and DbContext patterns see `.claude/skills/ef-core-data-access/SKILL.md`; for distributed cache strategy see `.claude/skills/redis-caching/SKILL.md`.
