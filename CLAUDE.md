# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Strict Rules

- **NEVER run `git commit`, `git push`, `git tag`, or `git init`.** All version control actions are performed by the human only. This applies to the main session and every subagent, and is additionally enforced by deny rules in `.claude/settings.json`.
- Never claim a build/test/exit criterion passed without running the command and seeing the output.
- **FREE ONLY — no paid services, no Docker.** Every component must run at zero cost using free/open-source software or genuinely free tiers. Never recommend, provision, or wire a paid service (Azure SQL, Azure Cache for Redis, Azure Blob, Azure SignalR, paid OpenAI API, paid hosting) or anything requiring Docker. Use the free substitutions in the table below. If a task seems to need a paid/Docker component, pick the free alternative and note it — do not silently introduce cost.
- **LOCAL ONLY — no subscriptions, no cloud accounts.** Everything runs on this machine with local, free tooling. Never require or assume a subscription, cloud account, or hosted platform. Deployment stays local/self-hosted and plans-only. The **devops-agent is NOT used on this project** (user preference); its purely-local duties — local build/run scripts, dev-environment setup, Options wiring, `/health` — are handled by **backend-agent**.

## Free-Only Infrastructure Constraint

The specs name several paid or Docker-based services. They are replaced by free, no-Docker equivalents. **Keep the Clean Architecture abstraction (port/interface) so a paid provider *could* be swapped in later without rework — but ship only the free provider.**

| Concern | Spec named (paid/Docker) | Free replacement — no Docker | Abstraction |
|---|---|---|---|
| SQL Server (dev) | Azure SQL / Docker SQL | **`Server=localhost`** (SQL Server 2022 Developer, default instance, Windows auth) → `CinoraDev`. Integration tests instead use **LocalDB** `(localdb)\MSSQLLocalDB` → `CinoraTest`. | EF Core `DbContext` |
| SQL Server (prod) | Azure SQL | **SQL Server Express** (free, running) | same |
| Cache | Azure Cache for Redis | **`AddDistributedMemoryCache()`** (built-in, free); optional **Memurai Developer** (free Windows Redis) if a real Redis is wanted | `IDistributedCache` |
| Blob storage (dev) | Azure Blob | **Azurite** emulator (`npm i -g azurite`, no Docker) | `IFileStorage` port |
| Blob storage (prod) | Azure Blob | **Local filesystem provider** under `wwwroot`/app data | `IFileStorage` port |
| AI recommendations | OpenAI (paid) | **Ollama** (free, local, native Windows, OpenAI-compatible `/v1` API — e.g. `llama3.2`/`phi3.5`/`qwen2.5`); free-tier cloud (Gemini/Groq) as alternative. Prefer **Microsoft.Extensions.AI** for a provider-agnostic client. | `IRecommendationEngine` port |
| Movie data | TMDB | **TMDB free API** (free key, non-commercial) — unchanged | typed `ITmdbClient` |
| Auth | Google OAuth | **Google OAuth** — free credentials — unchanged | ASP.NET Identity |
| Realtime | Azure SignalR | **Self-hosted SignalR** (free); no backplane unless scaling | hub |
| Background jobs | Hangfire | **Hangfire OSS** + SQL Server storage (free) — unchanged | — |
| Push | — | **Web Push + self-generated VAPID keys** (free) — unchanged | — |
| Hosting | Azure App Service | **Local / free tier only**; deployment stays plans-only (never executed) | — |

Default AI provider is **Ollama** (fully local, no key, no rate limits). This is a Phase 5 decision and revisitable; the `IRecommendationEngine` port keeps it swappable.

## Agent System

The engineering process from `Prompt.txt` is decomposed into a Claude Code multi-agent setup under `.claude/`:

- **Master Orchestrator** — `/orchestrate` (`.claude/commands/orchestrate.md`). Entry point for all substantive work: reads specs and `PROGRESS.md`, plans milestones, delegates to specialists, validates outputs, and loops until production quality.
- **13 specialist agents** — `.claude/agents/*.md` (architecture, backend, frontend, database, security, performance, pwa, ai, testing, ux, devops, documentation, code-review). Every agent must end its work with the six-section report: Analysis, Recommendations, Implementation, Validation, Risks, Next Steps. code-review-agent is read-only.
- **30 reusable skills** — `.claude/skills/*/SKILL.md`. Domain reference cards (EF Core, MVC, Tailwind v4, PWA, testing, Azure, …). Agents consult the ones named in their definition before non-trivial work.
- **Automatic review loops** — `/review-architecture`, `/review-code`, `/review-security`, `/review-performance`, `/review-ui`. Each loops review → fix → re-review until zero Critical/High findings (max 3 iterations, then report honestly). **They run automatically after every milestone and at the end of every phase** — this is mandatory, not optional. Deferred Medium/Low findings accumulate in `REVIEW_BACKLOG.md`.
- **Phase-based execution** — `/run-phase <1-6>` executes a phase from `.claude/phases/`, verifying the previous phase's exit criteria first and finishing with the review gates. Progress is logged in `PROGRESS.md`.

## Current State

The solution is **built and running**. `PROGRESS.md` is the live source of truth (newest first, with a **"RESUME HERE"** block) — read it before starting work; the phase/milestone/test numbers below drift every milestone, so trust `PROGRESS.md` over this section. As of the last entry:

- **ALL SIX PHASES COMPLETE** — Cinora is **feature-complete**; every phase exit gate passed at 0 Critical / 0 High. **1 Foundation**, **2 Discovery** (TMDB pipeline → `/discover` rails → Search → Details), **3 Reviews & Social** (Reviews CRUD, Likes+Comments, Friends+profiles, the `/home` activity feed, Notifications + self-hosted SignalR realtime), **4 Watchlists & Profile** (`IFileStorage` avatar upload/serving, profile edit + settings + app-wide `_Avatar` seam, watchlist status control on every title surface, the `/watchlist` page, profile highlights), **5 AI Recommendations** (the `IRecommendationEngine`/Ollama boundary → grounded candidate-generate→rank→**hallucination guard** → `AIRecommendationHistory` persist + `IDistributedCache` serve + never-500 heuristic → Hangfire OSS nightly precompute + fail-closed admin `/jobs` → the For-You rail + `/recommendations` page; **serving makes ZERO LLM calls by construction** — the model runs only in the off-request precompute job), **6 Polish & Deployment** (6.1 PWA foundation — manifest + service worker + `/offline` + versioned update → 6.2 Web Push — VAPID + `IPushSender`/`IPushDispatch` ports + SW push handlers → 6.3 notification prefs — owned-VO + the first migration since Phase 4, additive → 6.4 perf polish — static compression + bundle −27% + avatar 304 + feed SQL truncation → 6.5 a11y/animation polish — View Transitions + CSP-safe styled delete-confirm + WCAG AA → 6.6 deployment readiness — DP-key-ring persistence + absent-VAPID boot Warning + `runbook.md` + `dotnet publish` verify + `e2e/` Playwright scaffolding, **PLANS-ONLY**). Phase 6 EXIT GATE **PASSED across all five dimensions** (CSP byte-unchanged verbatim, shipped graph vuln-free, one additive migration, WCAG AA, zero-LLM-on-request preserved). Design: `docs/architecture/phase-6-polish-deployment-design.md` + ADRs 0019/0020/0021.
- **Carried (human/CI, non-blocking — none is a code defect; all in `docs/deployment/runbook.md §10`):** the browser/live-model smokes — **5.5 live-Ollama** For-You (`ollama serve` + `ollama pull llama3.2` → trigger `/jobs`), 6.1 PWA install/offline/update, 6.2 real push RFC-8291 round-trip, 6.3 push-settings/muted-type, 6.4/6.6 Lighthouse/CWV, 6.5 keyboard/reduced-motion; the **`e2e/` Playwright run** against a Release app (authored + compiling, run is carried); and the `CinoraDev`/Express migration apply.
- Last verified-green state: `dotnet build` **0 warnings / 0 errors** (TreatWarningsAsErrors ON), `npm run build` clean (`tsc --noEmit` strict, incl. the service-worker `tsconfig.sw.json`), **464 tests** pass (Domain 56, Application 128, Infrastructure.Tests 110, Web.IntegrationTests 170), **no NU19xx** in the shipped graph (a known test-only SQLite advisory in `Application.Tests` is backlogged), **CSP byte-unchanged (SHA-256 identical since Phase 2)**. Check the "RESUME HERE" block in `PROGRESS.md` for the current count — it moves every milestone.
- Still not a git repository. The free **TMDB v4 key** is set + live-verified in user-secrets. **In-app chat is resolved: deferred** (ADR 0010 — PRD-only, absent from the schema; build without it unless the user opts in). Remaining open user input (non-blocking): optional free **Google OAuth** creds to verify the Google sign-in path.
- **Operational caveat:** the migrations are applied to the integration-test DB `CinoraTest` (LocalDB) but **`CinoraDev` may be behind** — the `localhost` SQL Server instance was unreachable across recent sessions, so apply `artifacts/migrate.sql` (reviewed, idempotent) or `scripts/db-update.ps1` when the instance is up. The app never auto-migrates.

`REVIEW_BACKLOG.md` holds deferred Medium/Low review findings and cross-milestone contracts (e.g. the fail-closed authorization and opt-out anti-forgery rules that every new endpoint must honor).

## Common Commands

Run from the repo root (`D:\MyProject\Cinora\Cinora\Cinora`). PowerShell 7+; the `scripts/` helpers are PowerShell.

```powershell
# Build / test the whole solution (TreatWarningsAsErrors is ON — warnings fail the build)
dotnet build Cinora.sln -c Release
dotnet test  Cinora.sln -c Release

# One test project
dotnet test tests/Cinora.Domain.Tests/Cinora.Domain.Tests.csproj

# A single test (or a namespace/class) by fully-qualified name substring
dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj --filter "FullyQualifiedName~Registration"

# Front-end assets (Tailwind v4 CLI + esbuild via build.mjs → hashed/minified wwwroot/dist + manifest.json)
npm ci    --prefix src/Cinora.Web     # first run / lockfile changed
npm run build --prefix src/Cinora.Web # typecheck (tsc --noEmit) then bundle

# Run locally with hot reload (dotnet watch + npm watch); prints URLs, /health at :5148/health
scripts/dev.ps1
scripts/db-update.ps1                  # create/upgrade the local DB — idempotent, safe to re-run

# EF Core migrations (migrations live in Infrastructure; Web is the startup project)
dotnet ef migrations add <Name> --project src/Cinora.Infrastructure --startup-project src/Cinora.Web
dotnet ef database update       --project src/Cinora.Infrastructure --startup-project src/Cinora.Web
dotnet ef migrations script --idempotent --project src/Cinora.Infrastructure --startup-project src/Cinora.Web -o migrate.sql
```

The app **never auto-migrates on startup** — apply the reviewed idempotent SQL script (checked in at `artifacts/migrate.sql`, or regenerate with the command above) or run `db-update.ps1` instead. Integration tests run against a real LocalDB database `CinoraTest` (created/migrated automatically, isolated from your `CinoraDev`); they use the real EF+MVC+Identity pipeline, not an in-memory provider, and must target an `https://localhost` base address (the auth cookie is `Secure`).

## Code Architecture (as built)

Four production projects under `src/` enforce the Clean Architecture dependency rule **by compiler-checked project references** (Domain → nothing; Application → Domain; Infrastructure → Application; Web → Application + Infrastructure). Four `tests/` projects mirror the layers. `Cinora.Web` is the composition root (`Program.cs`). Understanding these non-obvious, cross-file conventions saves time:

- **Hand-rolled mediator, NOT MediatR.** `ISender`/`IRequest<T>`/`IRequestHandler<,>`/`IPipelineBehavior<,>`/`Sender` live in `Cinora.Application/Common/Messaging` (ADR 0005 — MediatR went commercial, banned by the free-only rule). `AddApplication()` assembly-scans and registers handlers, FluentValidation validators, and the behavior chain in execution order **Logging → Validation → Performance** (Logging outermost). Controllers inject `ISender`. **Do not add the MediatR package.**
- **`FluentAssertions` is also banned** (commercial since v8). Tests use xUnit's built-in `Assert` + `NSubstitute`. All package versions are pinned centrally in `Directory.Packages.props` (Central Package Management — `.csproj` files list packages by name only).
- **Data access via `IAppDbContext`, no generic repository.** `CinoraDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>` (in `Infrastructure/Persistence`) implements the Application-layer `IAppDbContext`. Reads are `AsNoTracking().Select(...)` projections; writes load a tracked entity, call its domain method, `SaveChangesAsync`. Entity mappings are one `IEntityTypeConfiguration<T>` per entity in `Persistence/Configurations`. Application references the EF Core **abstractions** package only (the one bounded exception to the dependency rule) — never the SQL Server provider or Infrastructure types.
- **Two-row user model.** `ApplicationUser` (Infrastructure/Identity, auth surface) shares its primary key with the domain `User` (users are referenced elsewhere by `Guid`, not navigation). Registration creates both rows atomically via `IUserRegistrationService` + an explicit `IDbContextTransaction`. Because of that user-managed transaction, **`EnableRetryOnFailure` must stay OFF** on the DbContext.
- **TMDB pipeline (Phase 2).** `ITmdbClient` port (Application) returns Application read models; Infrastructure's `TmdbClient` is a typed `HttpClient` (v4 Bearer, `AddStandardResilienceHandler`), wrapped by `CachedTmdbClient` (cache-aside over `IDistributedCache`, in-memory free provider; never caches null, degrades rather than throws). Catalog is persisted through `SyncGenresCommand` + `EnsureTitleCachedCommand` writing via `IAppDbContext` directly (the `ITitleCatalog` port was intentionally superseded — ADR 0008). TMDB DTOs are `internal` to Infrastructure and never cross into Domain. A controller that displays a title dispatches its read query **then** `EnsureTitleCachedCommand` (first-touch `Movie` persistence, cache-warm so no extra TMDB call) inside a graceful try/catch — this **read-then-`EnsureTitleCached`-then-write** orchestration is the standard entry point for any feature that writes against a title (reviews, watchlist, ADR 0007/0008).
- **Current-user + resource-ownership seam (Phase 3, ADR 0009).** `ICurrentUser` port (Application) is implemented by `CurrentUser` (Infrastructure/Identity, reads `IHttpContextAccessor` → `NameIdentifier`); commands resolve the actor **server-side** from it — controllers never bind an author/owner id. Ownership is enforced **inside the handler**: a non-owner throws `ForbiddenAccessException` (→ **403** arm on `GlobalExceptionHandler`), a missing row throws `NotFoundException` (→ 404). A per-user **`social-write`** rate-limit policy guards the review/comment/friend/watchlist write POSTs (registered *after* `UseAuthentication` so it keys on the user, not the IP).
- **Realtime (Phase 3, ADR 0011).** `IRealtimeNotifier` port (Application) + `NotificationDto`; the `Notification` row is co-persisted in the triggering handler's own `SaveChanges` (atomic), then a shared `RealtimeNotificationDispatcher` pushes **best-effort** (try/catch → Warning; a push failure never fails the write). Web hosts `NotificationHub` (`[Authorize]`, group-per-user `user-{id}`) + the `SignalRRealtimeNotifier` adapter over `IHubContext`; `@microsoft/signalr` is bundled locally (no CDN, no backplane). Any UI that renders `Notification.Message` must Razor-encode it (the toast uses `textContent`) — it stores the actor's raw `DisplayName`.
- **File storage / avatars (Phase 4, ADR 0013 — the free-local stand-in for "Azure Blob + SAS").** `IFileStorage` port (`SaveAsync(stream, declaredContentType, ct)`, pure `GetUrl(key, ttl)` so views may call it, idempotent `DeleteAsync`) + `IUploadPolicy` (surfaces the cap/allow-list to the Application validator without an Application→Infrastructure Options dependency). Infrastructure's `LocalFileStorage` is the authoritative upload gate (streamed size cap, dependency-free magic-byte sniff, server-minted key, writes under `App_Data/uploads` **outside `wwwroot`**); `MediaController` serves bytes only via an HMAC-signed, bucketed-expiry token (SAS stand-in) → 404 on any verification failure. Controllers unwrap `IFormFile` to a `Stream` in the Web layer — `IFormFile` never crosses into Application. The shared `_Avatar` Razor partial (`AvatarViewModel`) renders avatars app-wide and degrades to a monogram (never 500s a render).
- **Configuration via Options, free-provider names.** `TmdbOptions`/`AiOptions`/`CacheOptions`/`FileStorageOptions`/`GoogleAuthOptions` in `Infrastructure/Options`, each with a `SectionName` const, bound in `AddInfrastructure` and validated by the package-free `ValidateUsingDataAnnotations()`. Validation is lazy; `ValidateOnStart` is switched on only in the phase that first consumes each option. **Never inject `IConfiguration` into services** — bind to an Options class. Secrets go in `dotnet user-secrets` (dev), never `appsettings.json`.
- **Security wiring in `Program.cs`.** Fail-closed global authorization (a fallback policy requires auth; `[AllowAnonymous]` is the explicit opt-out), global `AutoValidateAntiforgeryToken` (HTMX writes send the token via the `RequestVerificationToken` header), `SecurityHeadersMiddleware` + strict CSP, and a per-IP `"auth"` rate-limit policy on the auth POSTs. `UseSerilogRequestLogging()` is registered **outermost** (so it records the exception handler's final translated status), then `UseExceptionHandler()` → `GlobalExceptionHandler` maps exceptions to RFC-7807 ProblemDetails (ValidationException→400, NotFoundException→404, DomainException→400, else 500 with no stack trace in the body).
- **Environment names are exact.** Behavior keys off `Development` / `Testing` / `Production` **spelled exactly**; a typo like `Prod` logs a startup Warning and would leave Dev/Testing-only diagnostics endpoints exposed. Diagnostics endpoints exist only in Development/Testing.
- **Front-end asset pipeline.** Source in `src/Cinora.Web/Styles/app.css` (Tailwind v4 `@theme` dark-luxury tokens) and `Scripts/site.ts`; `build.mjs` emits content-hashed, minified assets into `wwwroot/dist` with a `manifest.json` resolved at runtime via `IAssetManifest` (`@inject` in the layout). Alpine.js + HTMX are bundled locally (no CDN). The MSBuild `BuildFrontendAssets` target runs `npm ci && npm run build` on publish (or when the manifest is missing), so a plain inner-loop `dotnet build` with assets present is a no-op.

- **AI recommendations (Phase 5, ADR 0016 — the free-local stand-in for "OpenAI recommendations").** `IRecommendationEngine` port (Application, `RankAsync(RecommendationRequest, ct)`) over the `Common/Ai` read-models (`RecommendationRequest`/`TasteProfile`/`RatedTitle`/`CandidateTitle`/`RankedPick`/`AiUsage`/`RecommendationEngineResult`) — **Cinora/Domain types only**, no vendor type leaks. Infrastructure's `OllamaRecommendationEngine` uses `Microsoft.Extensions.AI` (an `IChatClient` from the `OpenAI` client pointed at local Ollama `/v1` — `http://localhost:11434/v1`, dummy key, **never `api.openai.com`**, so a Gemini/Groq swap is registration-only). The prompt is **PII-free** (titles/genres/ratings only, no id/name/email) and `AiLog` is **metrics-only** (never the prompt/response body). Malformed/empty model output **degrades to an empty pick set** (not a throw); `RecommendationEngineException` is reserved for transport/timeout. Treat `RankedPick.Reason` as untrusted model output — default Razor encoding, never `Html.Raw`. **AI packages are Infrastructure-only** (dependency rule); Ollama isn't running in this environment, so the live-model smoke is a deferred manual step.

Design records live in `docs/architecture/` (`solution-structure.md` is the authoritative Phase 1 layout; `phase-2-discovery-design.md`, `phase-3-social-design.md`, `phase-4-watchlists-profile-design.md`, `phase-5-ai-design.md`, `phase-6-polish-deployment-design.md`) and `docs/adr/0001`–`0021` (0009 resource-ownership, 0010 in-app-chat-deferred, 0011 self-hosted SignalR, 0012 activity-feed read-time query, 0013 file-storage/avatar-serving, 0014 watchlist-status resolution, 0015 notification-preferences-deferred, 0016 AI-provider/recommendation-engine-port, 0017 grounded-candidate-generation-and-ranking, 0018 AI-caching/history/fallback, 0019 PWA-service-worker, 0020 web-push, 0021 deployment-posture).

## Project Overview

**Cinora** — a premium social movie & series review platform, delivered as a Progressive Web App.

Core features: movie/series reviews with ratings out of 10, friends feed, in-app chat between users, likes, comments, watchlists, AI recommendations, push notifications, offline support.

## Source-of-Truth Documents

The `.docx` files in the repo root are the specs (note: `Prompt.txt` refers to them as `.md`, but they are Word documents). They are binary — the Read tool cannot open them. Extract text with PowerShell:

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead("D:\MyProject\Cinora\Cinora\Cinora\PRD.docx")
$reader = New-Object System.IO.StreamReader($zip.GetEntry('word/document.xml').Open())
($reader.ReadToEnd() -replace '</w:p>', "`n" -replace '<[^>]+>', '')
$zip.Dispose()
```

Their current content is brief; key facts:

- **PRD.docx** — vision, feature list (see overview above), constraints: ultra-premium UI, .NET 10, phase-wise implementation.
- **TRD.docx** — technical requirements (see stack below).
- **Backend_Schema.docx** — entities: `User, Friend, Movie, Genre, MovieGenre, Review, ReviewLike, Comment, Watchlist, Notification, Device, AIRecommendationHistory`.
- **App_Flow.docx** — Landing → Login → Home → Search → Movie Details → Review → Feed → Notifications → Profile.
- **Design_Brief.docx** — luxury streaming-platform theme: dark UI, glassmorphism, premium typography, responsive, smooth animations.
- **Implementation_Plan.docx** — phases: 1 Foundation, 2 Discovery, 3 Reviews, 4 Watchlists, 5 AI, 6 Polish & Deployment.
- **Prompt.txt** — the orchestration brief: full stack details, coding standards, and the expected execution model (plan and validate architecture before generating code; work milestone by milestone; review and iterate).

Known spec inconsistency: the PRD lists in-app chat, which is absent from Prompt.txt's feature list and the backend schema (no chat/message entity). **Resolved — deferred (ADR 0010):** build without chat unless the user explicitly opts in.

## Intended Technology Stack

Free-only realization (see the substitution table above for the reasoning):

- **Backend:** ASP.NET Core 10 MVC, EF Core, **SQL Server via LocalDB/Express (free)**, Clean Architecture, CQRS where appropriate, DDD principles
- **Frontend:** Razor views, Tailwind CSS v4, TypeScript, Alpine.js (or HTMX where appropriate)
- **Infrastructure:** self-hosted SignalR, Hangfire OSS (SQL storage), **cache via `IDistributedCache` (in-memory free; Redis-compatible optional)**, **`IFileStorage` (Azurite/local filesystem, free)**
- **Auth:** ASP.NET Identity + Google OAuth (free credentials)
- **External APIs:** TMDB (free tier), **local/free LLM via Ollama or a free-tier API (not paid OpenAI)** behind an `IRecommendationEngine` port

## Coding Standards (from Prompt.txt)

- Microsoft naming conventions; SOLID, DRY, KISS, YAGNI
- Async everywhere; dependency injection throughout
- FluentValidation for validation; Options Pattern for configuration
- Global exception handling and proper logging
- Repository Pattern only where it adds value
