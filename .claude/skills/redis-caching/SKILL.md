---
name: redis-caching
description: Use when adding, tuning, or debugging caching in Cinora — slow repeated reads (TMDB lookups, hot feeds), choosing keys and TTLs, cache stampedes, or cache-unreachable errors. Free default is in-memory IDistributedCache; Redis/Memurai optional.
---

# Caching (IDistributedCache — free, no Docker)

## Overview
Cinora codes every cache against `IDistributedCache`, a disposable read-through accelerator, never a source of truth. The **free default is `builder.Services.AddDistributedMemoryCache()`** — built-in, no server, no Docker. A real Redis is optional; to run one on Windows without Docker, install **Memurai Developer** (free) and register `AddStackExchangeRedisCache` — no handler changes, because the cache-aside pattern and `cinora:` key convention sit on `IDistributedCache`. Every cached value must be reconstructable from SQL Server or TMDB, and every cache failure must degrade to the slow path, not an error page.

## Quick Reference
| Task | Approach |
|---|---|
| Free default (dev + prod) | `builder.Services.AddDistributedMemoryCache()` — no server, no Docker |
| Optional real Redis | Memurai Developer (free, Windows) + `AddStackExchangeRedisCache` — swap at registration only |
| Cache API in handlers | Depend on `IDistributedCache` only — works unchanged on memory or Redis |
| Key convention | `cinora:{entity}:{id}` — e.g., `cinora:movie:42`, `cinora:feed:hot:page:1` |
| TMDB responses | TTL 6–24 h (metadata changes rarely) |
| Hot feed pages / trending | TTL 30–60 s (freshness matters) |
| Stampede protection | Per-key lock or jittered TTLs on hot keys |
| Cache down | Log warning, fall through to source |

## Pattern
```csharp
// Program.cs — FREE default (no server, no Docker):
//   builder.Services.AddDistributedMemoryCache();
// To use a real Redis later, swap ONLY this line (Memurai on Windows, no Docker):
//   builder.Services.AddStackExchangeRedisCache(o => { o.Configuration = conn; o.InstanceName = "cinora:"; });
// The handler below is identical either way — it depends on IDistributedCache, not any Redis type.
public sealed class CacheService(IDistributedCache cache, ILogger<CacheService> logger)
{
    public async Task<T?> GetOrCreateAsync<T>(
        string key, TimeSpan ttl, Func<Task<T?>> factory, CancellationToken ct)
    {
        try
        {
            var hit = await cache.GetStringAsync(key, ct);
            if (hit is not null) return JsonSerializer.Deserialize<T>(hit);
        }
        catch (Exception ex)
        {
            // WHY: a cache outage must never take Cinora down — degrade to the source
            logger.LogWarning(ex, "Cache read failed for {CacheKey}; using source", key);
        }

        var value = await factory(); // SQL Server or TMDB
        if (value is not null)
        {
            try
            {
                await cache.SetStringAsync(key, JsonSerializer.Serialize(value),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cache write failed for {CacheKey}", key);
            }
        }
        return value;
    }
}
// Usage: cacheService.GetOrCreateAsync($"cinora:movie:{id}", TimeSpan.FromHours(6), ...)
```

## Advanced Redis features (optional only)
`IDistributedCache` covers get/set/remove — enough for cache-aside on either the memory or Redis provider, so the free build depends only on it. Sorted sets (trending leaderboard), `StringIncrementAsync` (view/rate counters), key-pattern invalidation, and pub/sub need real Redis via a singleton `IConnectionMultiplexer`; treat these as optional enhancements that require Memurai, since the in-memory provider does not support them. The optional SignalR Redis backplane (`.claude/skills/signalr-realtime/SKILL.md`) would share the same Memurai instance.

## Stampede Protection
When a hot key (front-page trending list) expires, hundreds of requests hit the factory at once. Guard hot keys with a per-key `SemaphoreSlim` so one caller repopulates while the rest wait briefly, and add ±10% random jitter to TTLs so keys don't expire in unison.

## Common Mistakes
| Mistake | Fix |
|---|---|
| Hard-depending on `IConnectionMultiplexer` / StackExchange.Redis types in handlers | Depend on `IDistributedCache`; keep Redis types out of the free path |
| Assuming Redis must be running in dev | In-memory `AddDistributedMemoryCache()` is the free default — nothing to start |
| Reaching for Docker to run Redis | Not allowed — use in-memory, or Memurai Developer on Windows |
| No TTL on entries | Always set `AbsoluteExpirationRelativeToNow`; the cache is not durable storage |
| User-specific data under shared keys | Include the user ID: `cinora:watchlist:{userId}` |
| Letting cache exceptions bubble to users | Catch, log, serve from source |
| Caching before validating | Never cache error/empty TMDB responses (see `.claude/skills/tmdb-api-integration/SKILL.md`) |
