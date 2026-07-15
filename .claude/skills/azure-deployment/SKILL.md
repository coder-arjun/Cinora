---
name: azure-deployment
description: Use when planning Cinora's free/self-hosted deployment or writing deployment scripts — local/free-tier hosting, secrets and connection strings, idempotent EF migration scripts, health checks, or publish output. Deployment stays plans-only; the human executes.
---

# Deployment (free / self-hosted)

## Overview
Despite the directory name, this now means **free / self-hosted deployment**: Cinora runs locally or on genuinely free tiers only, at zero cost and without Docker. The free stack is **SQL Server Express/LocalDB**, **in-memory `IDistributedCache`** (or free Memurai), **local-filesystem/Azurite `IFileStorage`**, and **self-hosted SignalR** (no backplane unless scaling). Azure's free **F1 App Service** tier exists as an option but is not required and is never the default — prefer a local/self-hosted run. Hard rule: agents produce **plans and scripts only — never deploy, never push, never `git`**; deployment happens only when the human explicitly runs it. Schema changes ship as an idempotent EF SQL script applied manually.

## Quick Reference
| Task | Approach |
|---|---|
| Hosting | Local / self-hosted (free); Azure F1 free tier optional — never a paid tier |
| Database | SQL Server Express (prod) / LocalDB (dev) — free; never Azure SQL |
| Cache | In-memory `AddDistributedMemoryCache()` (free); Memurai optional — not Azure Cache for Redis |
| File storage | Local filesystem / Azurite `IFileStorage` — not Azure Blob |
| Realtime | Self-hosted SignalR; no backplane unless scaling past one instance |
| Configuration | Env vars (`__` for nesting) / user-secrets; never secrets in appsettings files |
| Migrations | `dotnet ef migrations script --idempotent` applied manually — never `Database.Migrate()` on startup |
| Health checks | `MapHealthChecks("/health")` with a SQL check |
| Publish | `dotnet publish -c Release -o ./publish` |
| Golden rule | Plans and scripts only — the human triggers every deploy; agents never `git` |

## Pattern
```csharp
// Program.cs — configuration-driven; works locally and on any free host.
// WHY: env vars / user-secrets override appsettings.json so no secret enters the repo;
// "ConnectionStrings__Default" points at SQL Express or LocalDB (both free).
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "Set ConnectionStrings__Default (SQL Express/LocalDB) in configuration.");

builder.Services
    .AddHealthChecks()
    .AddSqlServer(connectionString, name: "sql"); // free in-memory cache needs no health check

// WHY: no Database.Migrate() here — startup migration races across instances and is hard
// to roll back. Schema changes ship as an idempotent script applied MANUALLY by a human:
//   dotnet ef migrations script --idempotent \
//     -p src/Cinora.Infrastructure -s src/Cinora.Web -o artifacts/migrate.sql

var app = builder.Build();
app.MapHealthChecks("/health");
app.Run();
```

Script generation and idempotency details live in `.claude/skills/ef-core-migrations/SKILL.md`; options binding and secret handling in `.claude/skills/dotnet-configuration-options/SKILL.md`. Free cache and file-storage providers: `.claude/skills/redis-caching/SKILL.md` and `.claude/skills/azure-blob-storage/SKILL.md`; self-hosted realtime in `.claude/skills/signalr-realtime/SKILL.md`.

## Common Mistakes
| Mistake | Fix |
|---|---|
| Defaulting to a paid App Service / Azure SQL tier | Free/local only — SQL Express/LocalDB, in-memory cache, self-hosted SignalR |
| Auto-applying migrations on startup in prod | Apply the idempotent SQL script manually at deploy time |
| Committing/pushing or actually deploying | Plans and scripts only — agents never `git` or deploy; the human executes |
| Secrets in `appsettings.Production.json` | Env vars / user-secrets; config files hold only non-secrets |
| `:` in Linux environment variable names | Use `__` as the section separator |
| Scaling to 2+ instances with in-process SignalR | Add a free Redis (Memurai) backplane first — still no Docker |
