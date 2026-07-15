---
name: hangfire-background-jobs
description: Use when adding or troubleshooting background work in Cinora — scheduling recurring jobs like the nightly TMDB sync, offloading slow work from requests, job retries and duplicates, or Hangfire dashboard access.
---

# Hangfire Background Jobs

## Overview
Hangfire persists jobs to SQL Server and executes them on server workers with automatic retries — so job arguments must be small serializable IDs and every job body must be safe to run twice.

## Quick Reference
| Task | Approach |
|---|---|
| Fire-and-forget | `BackgroundJob.Enqueue<TJob>(j => j.RunAsync(id))` — e.g., send a like notification |
| Delayed | `BackgroundJob.Schedule<TJob>(j => j.RunAsync(id), TimeSpan.FromMinutes(10))` |
| Recurring | `RecurringJob.AddOrUpdate<TJob>("key", j => j.RunAsync(), Cron.Daily(3))` |
| Storage | `UseSqlServerStorage` (own connection string, shared DB is fine) |
| Dashboard | `MapHangfireDashboard` + `IDashboardAuthorizationFilter` |
| Retries | Automatic (10 attempts default); design jobs idempotent |

## Pattern
```csharp
builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("CinoraDb")));
builder.Services.AddHangfireServer();

// WHY: the dashboard exposes job arguments and lets users trigger/delete jobs —
// it must never be reachable anonymously in production
app.MapHangfireDashboard("/jobs", new DashboardOptions
{
    Authorization = [new AdminDashboardAuthorizationFilter()]
});

// Recurring: nightly TMDB catalog sync and recommendation precompute
RecurringJob.AddOrUpdate<TmdbSyncJob>(
    "tmdb-nightly-sync", j => j.RunAsync(CancellationToken.None), Cron.Daily(3));
RecurringJob.AddOrUpdate<RecommendationPrecomputeJob>(
    "ai-recs-precompute", j => j.RunAsync(CancellationToken.None), Cron.Daily(4));

// Fire-and-forget from a handler — WHY pass IDs, not entities: arguments are
// JSON-serialized into SQL storage; entities are stale by execution time,
// bloat storage, and break when the schema changes
BackgroundJob.Enqueue<ReviewNotificationJob>(
    j => j.NotifyReviewLikedAsync(reviewId, likerUserId, CancellationToken.None));
```

Job classes are plain services resolved from DI, so constructor-inject `CinoraDbContext`, typed clients, and `ILogger` as usual — Hangfire creates a scope per execution. Reload the entity by ID inside the job.

## Idempotency and Retries
A failed job re-runs up to 10 times with backoff, and a worker crash can re-execute a job that already partially completed. Make handlers idempotent: check whether the `Notification` row already exists before inserting, upsert TMDB records by `TmdbId`, and use `[DisableConcurrentExecution]` or a unique index — never assume exactly-once. Use `[AutomaticRetry(Attempts = 0)]` only for jobs that are safe to drop.

## Common Mistakes
| Mistake | Fix |
|---|---|
| Passing entities/DTO graphs as job args | Pass IDs; reload inside the job |
| Non-idempotent job + default retries | Duplicate notifications/emails — upsert or check-before-insert |
| Anonymous dashboard in production | Require an admin policy via `IDashboardAuthorizationFilter` |
| Capturing scoped services in the enqueue lambda | Enqueue a method on a DI-resolved job class instead |
| Assuming local time in cron | Hangfire schedules in UTC unless a `TimeZoneInfo` is supplied |

Jobs that call external APIs should reuse the typed clients from `.claude/skills/tmdb-api-integration/SKILL.md` and `.claude/skills/openai-recommendations/SKILL.md`; push results to browsers via `IHubContext` (see `.claude/skills/signalr-realtime/SKILL.md`).
