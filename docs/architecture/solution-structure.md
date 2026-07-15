# Cinora — Phase 1 Solution Architecture (Authoritative)

- **Status:** Accepted for Phase 1 (Foundation)
- **Date:** 2026-07-02
- **Owner:** architecture-agent
- **Related ADRs:** [0001 Layering](../adr/0001-clean-architecture-layering.md),
  [0002 LocalDB / no Docker](../adr/0002-local-dev-database-localdb-no-docker.md),
  [0003 ApplicationUser placement](../adr/0003-applicationuser-identity-placement.md),
  [0004 Free / local-only infrastructure](../adr/0004-free-local-only-infrastructure.md),
  [0005 Hand-rolled mediator](../adr/0005-hand-rolled-mediator.md)

This document is the single source of truth for the Phase 1 solution layout, dependency
direction, package choices, and where every Phase 1 concern lives. Implementation agents
follow it; deviations require an ADR.

> **Two cross-cutting constraints govern everything below (added in the Phase 1 doc reconciliation, 2026-07-02):**
> - **Free / local-only infrastructure — [ADR 0004](../adr/0004-free-local-only-infrastructure.md).**
>   All infrastructure is free, no-Docker, and local; the spec-named paid services are replaced by
>   free providers behind ports (in-memory `IDistributedCache`, local-filesystem/Azurite `IFileStorage`,
>   Ollama `IRecommendationEngine`, etc.). The authoritative table is `CLAUDE.md` → "Free-Only
>   Infrastructure Constraint". The Options names in §5/§6 (`AiOptions`, `CacheOptions`,
>   `FileStorageOptions`) reflect these free providers, not the paid ones.
> - **Hand-rolled mediator — [ADR 0005](../adr/0005-hand-rolled-mediator.md).** MediatR moved to a
>   commercial license (the risk in §8, realized), so it is **not used**. The `ISender`/`IRequest`/
>   `IRequestHandler`/`IPipelineBehavior` mediator is hand-rolled in
>   `Cinora.Application/Common/Messaging`. Every "mediator" reference below means this in-house type.

> **Environment (verified 2026-07-02, do not re-check):** .NET SDK 10.0.301, ASP.NET Core 10
> runtime, `dotnet-ef` 10.0.8, Node 22 / npm 10, SQL Server LocalDB `(localdb)\MSSQLLocalDB`,
> SQL Express running. **Docker is NOT installed.** Greenfield — no code exists.

---

## 1. Solution and project names (confirmed)

The names proposed in the phase brief are **confirmed as-is** — they match the Clean
Architecture skill, are unambiguous, and need no change.

```
Cinora.sln
├─ src/
│  ├─ Cinora.Domain            (class library, net10.0)
│  ├─ Cinora.Application       (class library, net10.0)
│  ├─ Cinora.Infrastructure    (class library, net10.0)
│  └─ Cinora.Web               (ASP.NET Core 10 MVC, net10.0)  ← startup / composition root
└─ tests/
   ├─ Cinora.Domain.Tests           (xUnit, net10.0)
   ├─ Cinora.Application.Tests      (xUnit, net10.0)
   └─ Cinora.Web.IntegrationTests   (xUnit + WebApplicationFactory, net10.0)
```

**Rationale:** four production projects express the four Clean Architecture layers with one
project boundary each, so the dependency rule is enforced by the compiler. The `src/` and
`tests/` split keeps the solution root clean and is the conventional .NET layout. Test-project
names mirror the layer they target: Domain and Application get fast unit tests; Web gets
end-to-end integration tests through the real MVC + EF + Identity pipeline. No separate
`Infrastructure.Tests` in Phase 1 — Infrastructure is exercised transitively by the Web
integration tests (against LocalDB); a dedicated Infrastructure test project can be added later
if adapter logic (TMDB/OpenAI clients) warrants isolated tests.

---

## 2. Target framework and shared build configuration

All seven projects target **`net10.0`** with the same compiler stance, centralized in a
`Directory.Build.props` at the repo root, plus **Central Package Management** via
`Directory.Packages.props` so every package version is pinned in one place.

| Property | Value | Why |
|---|---|---|
| `TargetFramework` | `net10.0` | Single supported runtime for the project |
| `LangVersion` | `latest` (C# 14) | Explicit intent; matches the SDK default |
| `Nullable` | `enable` | Null-safety across the codebase |
| `ImplicitUsings` | `enable` | Less boilerplate; consistent global usings |
| `TreatWarningsAsErrors` | `true` | Warnings are defects; keep the build honest |
| `EnforceCodeStyleInBuild` | `true` | `.editorconfig` style violations fail the build |
| `GenerateDocumentationFile` | `true` (src only) | Surfaces missing XML-doc / bad refs as warnings |
| `ManagePackageVersionsCentrally` | `true` | Versions pinned once in `Directory.Packages.props` |

### Proposed `Directory.Build.props` (repo root)

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>
```

> **Warnings-as-errors caveat (honest):** EF Core's generated migration files and some analyzer
> rules can trip `TreatWarningsAsErrors`. Keep the stance **on**; scope unavoidable noise narrowly
> with `#pragma warning disable` in the specific generated file or a targeted `<NoWarn>` in the
> owning `.csproj` — never relax the stance globally. Test projects keep TWAE on but may `<NoWarn>`
> xUnit analyzer rules that conflict with test style.

A shared `.editorconfig` at the repo root enforces Microsoft naming conventions (backing the
`EnforceCodeStyleInBuild` setting). devops-agent authors both files in Milestone 1.0.

---

## 3. The dependency rule and exact reference direction

Dependencies point **inward only**. This is inviolable (ADR 0001).

| Project | Project references (the ONLY edges allowed) |
|---|---|
| `Cinora.Domain` | **none** — references nothing of ours; ideally zero NuGet packages |
| `Cinora.Application` | `Cinora.Domain` |
| `Cinora.Infrastructure` | `Cinora.Application` (Domain transitively) |
| `Cinora.Web` | `Cinora.Application` **and** `Cinora.Infrastructure` |

- **Domain references nothing.** No EF Core, no Identity, no mediator (the hand-rolled mediator lives in Application — ADR 0005).
- **Application references Domain** (only). Plus the bounded NuGet exception below.
- **Infrastructure references Application.** It implements Application's interfaces
  (`IAppDbContext`, `ICurrentUser`, and future `ITmdbClient` etc.) and depends on Domain
  transitively.
- **Web references Application + Infrastructure.** Infrastructure is referenced **solely** so
  `Program.cs` can call `AddInfrastructure(...)` to compose the graph. Controllers/views use
  Application abstractions, not Infrastructure types.

**Bounded NuGet exception (ADR 0001):** `Cinora.Application` references the EF Core
**abstractions** package `Microsoft.EntityFrameworkCore` so it can define `IAppDbContext`
exposing `DbSet<T>` for read projections. It does **not** reference the SQL Server provider or
`Cinora.Infrastructure`. Infrastructure types must never appear in Application public signatures.

**Test edges:** `Cinora.Domain.Tests → Cinora.Domain`; `Cinora.Application.Tests →
Cinora.Application`; `Cinora.Web.IntegrationTests → Cinora.Web`.

---

## 4. Per-project NuGet packages

Version policy per the brief: packages that **track the runtime** (EF Core, ASP.NET Identity EF,
Google auth) are pinned to the **10.x line** (concretely 10.0.x, matching the installed
`dotnet-ef` 10.0.8). Third-party packages are recorded as **"latest stable"** with the expected
major line; **no version numbers are invented** where uncertain. All versions are pinned centrally
in `Directory.Packages.props`.

### `Cinora.Domain`
- **None.** Zero NuGet dependencies (goal). If a guard-clause helper is ever wanted, prefer
  hand-written guards over a package to keep Domain pristine.

### `Cinora.Application`
| Package | Version stance | Purpose |
|---|---|---|
| `FluentValidation` | **12.1.1** (pinned; Apache-2.0, free) | Validators for commands/queries |
| `FluentValidation.DependencyInjectionExtensions` | **12.1.1** (pinned) | `AddValidatorsFromAssembly` in DI |
| `Microsoft.EntityFrameworkCore` | **10.0.x** (abstractions only) | `IAppDbContext` exposing `DbSet<T>`, `IQueryable` async ops for reads |

> **No mediator NuGet package.** The mediator (`ISender`, `IRequest<TResponse>`,
> `IRequestHandler<,>`, `IPipelineBehavior<,>`, `Sender`) is **hand-rolled in-house** in
> `Cinora.Application/Common/Messaging` (ADR 0005) — MediatR is not referenced because it moved to a
> commercial license (§8), which the free-only constraint (ADR 0004) forbids. So Application's only
> NuGet dependencies are **FluentValidation (+ its DI extensions)** and the **EF Core abstractions**
> package. Do **not** add the SQL Server provider here.

### `Cinora.Infrastructure`
| Package | Version stance | Purpose |
|---|---|---|
| `Microsoft.EntityFrameworkCore.SqlServer` | **10.0.x** | SQL Server provider |
| `Microsoft.EntityFrameworkCore.Design` | **10.0.x** | Design-time services (also referenced by the startup project — see below) |
| `Microsoft.AspNetCore.Identity.EntityFrameworkCore` | **10.0.x** | Identity EF stores for `ApplicationUser` |

> `Microsoft.EntityFrameworkCore` (abstractions) arrives transitively via the SqlServer provider.
> Serilog sinks and options binding live where they are consumed (Web) — Infrastructure does not
> reference Serilog directly in Phase 1.

### `Cinora.Web` (startup / composition root)
| Package | Version stance | Purpose |
|---|---|---|
| `Microsoft.AspNetCore.Authentication.Google` | **10.0.x** | Google OAuth external login |
| `Serilog.AspNetCore` | latest stable (v8/v9 line) | Serilog host integration + `UseSerilogRequestLogging`; bundles the Console sink and `Serilog.Settings.Configuration` |
| `Serilog.Sinks.Console` | latest stable | Explicit console sink (dev) — also transitive via `Serilog.AspNetCore` |
| `Serilog.Sinks.File` | latest stable | Rolling file sink |
| `Microsoft.EntityFrameworkCore.Design` | **10.0.x** | Required in the **startup project** for `dotnet ef` (migrations target Infrastructure, startup is Web) |
| _(hand-rolled mediator)_ | none — comes via the `Cinora.Application` project reference | Controllers inject `ISender` (ADR 0005); it is an in-house type in Application, so no NuGet package is involved |

> **EF Tools note:** `dotnet-ef` is already installed as a tool (10.0.8). The
> `Microsoft.EntityFrameworkCore.Tools` **package** is only needed for the Visual Studio Package
> Manager Console; it is **optional** for CLI-driven migrations and is omitted unless PMC is
> desired. The **Design** package in the startup project (Web) is the requirement.

### Test projects (`*.Tests`, all net10.0, xUnit)
| Package | Version stance | Purpose |
|---|---|---|
| `Microsoft.NET.Test.Sdk` | latest stable | Test host |
| `xunit` / `xunit.runner.visualstudio` | latest stable (v2/v3 line) | Test framework + runner |
| `NSubstitute` | latest stable | Mocking (per xunit-testing skill) |
| `FluentAssertions` | latest stable — **verify license** (v8 changed licensing); assertions optional | Readable assertions; if licensing is a concern, use xUnit `Assert` |
| `Microsoft.AspNetCore.Mvc.Testing` | **10.0.x** | `WebApplicationFactory` (IntegrationTests only) |
| `Microsoft.EntityFrameworkCore.SqlServer` | **10.0.x** | Integration tests point EF at LocalDB (IntegrationTests only) |

> Per ADR 0002, integration tests run against **LocalDB** (Docker/Testcontainers unavailable).

---

## 5. Where each Phase 1 concern lives

| Concern | Location | Notes |
|---|---|---|
| **12 domain entities** — `User, Friend, Movie, Genre, MovieGenre, Review, ReviewLike, Comment, Watchlist, Notification, Device, AIRecommendationHistory` | `Cinora.Domain/Entities` | POCOs with behaviour; private setters; factory methods. Value object `Rating` (1–10) in `Cinora.Domain/ValueObjects`. Users referenced by `Guid UserId`. |
| **`Rating` value object (1–10 invariant)** | `Cinora.Domain/ValueObjects/Rating.cs` | Invariant enforced in `Rating.From(int)`; throws for out-of-range. Not in handlers. |
| **`CinoraDbContext` + `IEntityTypeConfiguration<T>`** | `Cinora.Infrastructure/Persistence` (+ `/Configurations`) | Derives from `IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>`. `ApplyConfigurationsFromAssembly` in `OnModelCreating`. |
| **Identity `ApplicationUser`** | `Cinora.Infrastructure/Identity` | `ApplicationUser : IdentityUser<Guid>` — auth surface only. See ADR 0003 (shared PK with `Domain.User`). |
| **`IAppDbContext` abstraction** | `Cinora.Application/Common/Interfaces` | Exposes domain `DbSet<>`s (`Users`, `Reviews`, …) + `SaveChangesAsync`. Implemented by `CinoraDbContext`. Never exposes `ApplicationUser`. |
| **`ICurrentUser`** | interface in `Cinora.Application/Common/Interfaces`; impl in `Cinora.Infrastructure` (reads `HttpContext`) | Handlers get the current user's `Guid` without touching HTTP. |
| **Hand-rolled mediator contracts** — `ISender`, `IRequest<T>`, `IRequestHandler<,>`, `IPipelineBehavior<,>`, `RequestHandlerDelegate<T>`, `Sender` | `Cinora.Application/Common/Messaging` | In-house mediator (ADR 0005), no MediatR. `Sender` caches a typed wrapper per request type; registered `ISender`→`Sender` (scoped) in `AddApplication()`. |
| **Mediator pipeline behaviors** — `LoggingBehavior`, `ValidationBehavior`, `PerformanceBehavior` | `Cinora.Application/Common/Behaviors` | Registered in `AddApplication()` in execution order **Logging → Validation → Performance** (Logging outermost, closest-to-handler last). `ValidationBehavior` no-ops when a request has no validators. |
| **FluentValidation validators** | `Cinora.Application/Features/<Feature>/` — colocated with each command/query | Registered via `AddValidatorsFromAssembly`. Phase 1 has ~zero features, so likely zero validators yet — pipeline is scaffolded regardless. |
| **Application exceptions** — `NotFoundException`, custom `ValidationException` | `Cinora.Application/Common/Exceptions` | Referenced by the Web global handler (Web → Application is allowed). |
| **Options classes** — `GoogleAuthOptions`, `TmdbOptions`, `AiOptions`, `CacheOptions`, `FileStorageOptions` | `Cinora.Infrastructure/Options` | One class per external concern, each with a `SectionName` const + data-annotation validation. Named for the **free** providers per ADR 0004 (`AiOptions`→Ollama, `CacheOptions`→in-memory/Memurai, `FileStorageOptions`→local filesystem/Azurite), not the paid ones. **Bound at the composition root** — see §6. |
| **Serilog setup** | `Cinora.Web/Program.cs` (bootstrap) + `appsettings*.json` | Two-stage init: bootstrap logger before `builder`, then `builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(...))`; `app.UseSerilogRequestLogging()`. Sinks: Console (dev) + rolling File. |
| **Global exception handling (`IExceptionHandler`)** | `Cinora.Web/Infrastructure/GlobalExceptionHandler.cs` | `AddProblemDetails()` + `AddExceptionHandler<GlobalExceptionHandler>()`; `app.UseExceptionHandler()` first in the pipeline. Maps `ValidationException`→400, `NotFoundException`→404, else 500 (details to logs only). |
| **DI composition extensions** — `AddApplication()`, `AddInfrastructure(IConfiguration)` | `AddApplication` in `Cinora.Application`; `AddInfrastructure` in `Cinora.Infrastructure` | `Program.cs` calls both. Options binding, DbContext, Identity, external adapters live in `AddInfrastructure`. |
| **Controllers, views, `Program.cs`, middleware, filters** | `Cinora.Web` | MVC. `AccountController` uses `UserManager`/`SignInManager` directly (see §7). `HomeController` renders the authenticated shell. |
| **Tailwind v4 + TypeScript build** | `Cinora.Web` — source in `Styles/` (`app.css`) and `Scripts/` (`site.ts`); output to `wwwroot/` | `package.json` at the Web project root. Tailwind CLI v4 (`@theme` dark tokens) + esbuild. Build emits **hashed, minified** assets into `wwwroot/dist` (or `wwwroot/css` + `wwwroot/js`) with a manifest for cache-busting. An MSBuild target runs `npm ci && npm run build` before `dotnet publish`. Details owned by frontend-agent (skills: `tailwind-v4`, `typescript-frontend`). |

**Anti-patterns explicitly banned (from the standards):**
- No generic `IRepository<T>`. Reads use `IAppDbContext` projections (`AsNoTracking().Select(...)`).
  True repositories are reserved for aggregate roots with real invariants — `Review`, `Watchlist`,
  `Friend` — and **none are created in Phase 1** (YAGNI; no such use cases exist yet).
- No business rules in handlers or behaviors — invariants live on entities / value objects.
- No `IConfiguration` injected into services — bind to Options classes.
- No external-API DTO ever crosses into `Cinora.Domain`.

---

## 6. Options Pattern placement and binding

- **Where the classes live:** `Cinora.Infrastructure/Options` — they describe the **free-provider**
  infrastructure adapters (`TmdbOptions`→TMDB free API, `AiOptions`→Ollama local LLM,
  `CacheOptions`→in-memory `IDistributedCache`/optional Memurai, `FileStorageOptions`→local
  filesystem/Azurite — all per ADR 0004) and auth (`GoogleAuthOptions`). This matches the
  configuration skill and keeps Domain/Application clean.
- **Where they are bound:** at the **composition root**. `Program.cs` (Web) calls
  `AddInfrastructure(builder.Configuration)`, and the binding
  (`AddOptions<T>().BindConfiguration(T.SectionName).ValidateUsingDataAnnotations()`) happens inside
  that extension. `ValidateUsingDataAnnotations()` is an **in-house, package-free** validator
  (`OptionsValidationExtensions`) equivalent to the framework's `ValidateDataAnnotations()` — the
  DataAnnotations options package is not referenced by Infrastructure. It runs **lazily** on first
  access and, with **no `ValidateOnStart` in Phase 1**, never at boot. So the *code* lives in
  Infrastructure but binding *executes* at Web startup — satisfying "bound in Web" while respecting
  the layer that owns the settings. `GoogleAuthOptions` is bound (without validation) and consumed by
  a conditional `AddGoogle(...)` in `Program.cs` only when both credentials are present.
- **Secrets:** `dotnet user-secrets` in Development (e.g. `GoogleAuth:ClientId`,
  `GoogleAuth:ClientSecret`); environment variables / Key Vault in production. Never in
  `appsettings.json`.
- **Phase 1 YAGNI on validation:** only `GoogleAuthOptions` is actually consumed this phase.
  `TmdbOptions`, `AiOptions`, `CacheOptions`, `FileStorageOptions` are **defined, bound, and lazily
  validated** (as the phase requires) but their `[Required]` enforcement is deferred to first access,
  and `ValidateOnStart` is **switched on in the phase that first consumes each** (Phase 2 TMDB,
  Phase 5 AI) — so a Phase 1 boot does not fail merely because a TMDB key is absent. `GoogleAuth` is
  bound without `ValidateOnStart`; email/password sign-in must boot without Google credentials, and
  `AddGoogle` is wired only when `GoogleAuthOptions.IsConfigured` (see PROGRESS "Open Decisions").

---

## 7. CQRS boundary policy for Phase 1 (lean by design)

Phase 1 has almost no use cases: Landing, Login/Register, and an empty authenticated Home shell.
The policy:

- **Establish the hand-rolled mediator pipeline (`ISender`), but do not manufacture features for
  it.** Register `AddApplication()` with the in-house mediator (ADR 0005) and the
  `LoggingBehavior` → `ValidationBehavior` → `PerformanceBehavior` chain so real features (Phase 2
  onward) slot in with zero plumbing. It is acceptable — expected — that Phase 1 ships with
  **zero** commands/queries.
- **Authentication is NOT routed through the mediator.** `AccountController` calls `UserManager` /
  `SignInManager` (Identity) directly for register, login, logout, and the external Google
  callback, delegating the atomic two-row account creation to `IUserRegistrationService` (ADR 0003).
  Identity is a framework service; wrapping it in commands adds ceremony with no Phase 1 value.
  (cqrs-mediatr skill: "When CQRS is overkill".)
- **First real CQRS use cases arrive in Phase 2** (Discovery/search over TMDB-backed data).
- **Reads:** query handlers project via `IAppDbContext` with `AsNoTracking()` + `Select` — no
  repositories. **Writes:** load a tracked entity, call its domain method, `SaveChangesAsync`.
- **Repositories:** none in Phase 1. Introduce one only for an aggregate root with genuine
  invariants when its first real use case appears (candidates: `Review`, `Watchlist`, `Friend`).

Guiding rule: use commands/queries where there is real orchestration or validation; reject
ceremony around trivial passthroughs; never introduce a generic `IRepository<T>`.

---

## 8. Key risks baked into the design

- **MediatR licensing — RESOLVED (ADR 0005).** The flagged risk was realized: MediatR moved to a
  **commercial license**, which the free-only constraint (ADR 0004) forbids. Resolution: a
  **hand-rolled minimal mediator** now lives in `Cinora.Application/Common/Messaging` behind
  `AddApplication()` (`ISender`/`IRequest`/`IRequestHandler`/`IPipelineBehavior`/`Sender`) — no
  MediatR package is referenced anywhere. (The same licensing reasoning applies to `FluentAssertions`
  v8 in test projects — it is **banned**; tests use xUnit's built-in `Assert`.)
- **`TreatWarningsAsErrors` vs generated code.** See §2 caveat — scope, don't disable.
- **Two-row user model (ADR 0003).** Registration must create `ApplicationUser` + `Domain.User`
  atomically; database-agent owns the transaction/mapping.
- **LocalDB is Windows-only.** CI/prod (Phase 6) needs Azure SQL or a container; the
  connection-string strategy (ADR 0002) already isolates this.

---

## 9. Phase 1 milestone build order (delegable)

Ordered milestones for the orchestrator to delegate. Each states the owning agent(s), the
deliverable, and the **exact command that proves it**. Run the mandatory review gates
(`/review-architecture`, `/review-code`, `/review-security`, `/review-ui`) after the relevant
milestones per `CLAUDE.md`. All commands run from the repo root `D:\Cinora` unless noted.

> Design-only note: **none of these commands have been run yet** (no code exists). They are the
> acceptance checks each implementing agent must execute and show output for.

### 1.0 — Solution skeleton (builds empty)
- **Owner:** devops-agent (scaffold) with architecture-agent review.
- **Deliverable:** `Cinora.sln`; four `src` projects + three `tests` projects with the reference
  edges of §3; `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`,
  `global.json` (pin SDK 10.0.x); empty `Program.cs` in Web.
- **Verify:**
  ```
  dotnet build Cinora.sln -c Release
  dotnet sln Cinora.sln list
  ```
  Build succeeds with zero warnings (TWAE on); `sln list` shows all seven projects.

### 1.1 — Domain entities + value objects
- **Owner:** backend-agent (domain behaviour) with database-agent consulted.
- **Deliverable:** the 12 entities in `Cinora.Domain/Entities`, `Rating` value object
  (1–10 invariant), factory methods + private setters. `Cinora.Domain.Tests` covering `Rating`
  bounds and entity factory invariants. No EF, no Identity in Domain.
- **Verify:**
  ```
  dotnet build src/Cinora.Domain/Cinora.Domain.csproj -c Release
  dotnet test tests/Cinora.Domain.Tests/Cinora.Domain.Tests.csproj
  ```
  Tests pass, including `Rating.From(0)` and `Rating.From(11)` throwing.

### 1.2 — EF Core DbContext + configs + initial migration on LocalDB
- **Owner:** database-agent.
- **Deliverable:** `CinoraDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>`
  implementing `IAppDbContext`; one `IEntityTypeConfiguration<T>` per entity (composite keys for
  `MovieGenre`/`ReviewLike`, unique indexes for one-review-per-user-per-movie and
  `Watchlist(UserId,MovieId)`, `Restrict` on `Friend` self-references); `ApplicationUser`↔`User`
  shared-PK mapping (ADR 0003); genre seed data; `InitialCreate` migration.
- **Verify:**
  ```
  dotnet ef migrations add InitialCreate --project src/Cinora.Infrastructure --startup-project src/Cinora.Web
  dotnet ef database update --project src/Cinora.Infrastructure --startup-project src/Cinora.Web
  ```
  Migration builds; `database update` creates the full schema on `(localdb)\MSSQLLocalDB`
  (verify tables exist, e.g. `dotnet ef migrations list` shows `InitialCreate` applied, or query
  the DB). `dotnet build` still clean.

### 1.3 — Identity + Google OAuth + Landing/Login
- **Owner:** security-agent (Identity/OAuth wiring) + frontend-agent (Landing/Login views) +
  backend-agent (`AccountController`, `HomeController`).
- **Deliverable:** `AddIdentity<ApplicationUser, IdentityRole<Guid>>().AddEntityFrameworkStores
  <CinoraDbContext>()`; `AddAuthentication().AddGoogle(...)` bound from `GoogleAuthOptions`;
  cookie policy (`SameSite=Lax`, sliding); register/login/logout + external callback creating
  `ApplicationUser` + `Domain.User` atomically; Landing page; Login page; authenticated Home shell.
- **Verify:**
  ```
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  Integration tests: register + email/password login reaches the authenticated Home shell;
  anonymous access to Home redirects to `/account/login`. **Google end-to-end requires real
  client id/secret in user-secrets** (per PROGRESS Open Decisions) — verified manually / deferred;
  the OAuth *wiring* is asserted (challenge redirects to Google) without live credentials.

### 1.4 — Hand-rolled mediator + FluentValidation pipeline + global exception handling + Serilog
- **Owner:** backend-agent.
- **Deliverable:** `AddApplication()` registering the hand-rolled mediator (ADR 0005) +
  `LoggingBehavior`/`ValidationBehavior`/`PerformanceBehavior` (in that execution order) +
  `AddValidatorsFromAssembly`; `NotFoundException`/`ValidationException`;
  `GlobalExceptionHandler : IExceptionHandler` + `AddProblemDetails()` + `UseExceptionHandler()`;
  two-stage Serilog with `UseSerilogRequestLogging()`, Console + rolling File sinks.
- **Verify:**
  ```
  dotnet test tests/Cinora.Application.Tests/Cinora.Application.Tests.csproj
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  Application tests: a sample command with a failing validator throws `ValidationException`
  through `ValidationBehavior`. Integration tests: a validation failure returns **400
  ValidationProblemDetails**; a forced unhandled exception returns **500 ProblemDetails** with no
  stack trace in the body; a request emits one Serilog request-log event.

### 1.5 — Options classes
- **Owner:** devops-agent (config wiring) with backend-agent.
- **Deliverable:** `GoogleAuthOptions`, `TmdbOptions`, `AiOptions`, `CacheOptions`,
  `FileStorageOptions` (free-provider names per ADR 0004) in `Cinora.Infrastructure/Options`, each
  with `SectionName` + data annotations; bound in `AddInfrastructure`; validated lazily via the
  in-house package-free `ValidateUsingDataAnnotations()`, with `ValidateOnStart` deferred to the
  phase that consumes each (§6); user-secrets set up for `GoogleAuth`.
- **Verify:**
  ```
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  A test asserts the app **boots** with valid config and that an **invalid required option fails
  fast at startup** (`ValidateOnStart`). Non-Phase-1 options do not block boot when unset.

### 1.6 — Tailwind v4 + TypeScript build + base layout shell
- **Owner:** frontend-agent (skills: `tailwind-v4`, `typescript-frontend`, `razor-views`,
  `premium-ui-design`).
- **Deliverable:** `package.json` in `src/Cinora.Web`; `Styles/app.css` (`@import "tailwindcss"`,
  `@theme` dark luxury tokens); `Scripts/site.ts`; Tailwind CLI + esbuild build → hashed,
  minified assets in `wwwroot`; `_Layout.cshtml` with dark theme, responsive nav shell; MSBuild
  target invoking `npm ci && npm run build` on publish.
- **Verify:**
  ```
  npm ci --prefix src/Cinora.Web
  npm run build --prefix src/Cinora.Web
  dotnet publish src/Cinora.Web/Cinora.Web.csproj -c Release -o artifacts/web
  ```
  `npm run build` emits hashed/minified CSS + JS into `wwwroot`; `dotnet publish` triggers the
  build target and the published `wwwroot` contains the hashed assets. Layout renders responsive
  with dark tokens (confirmed via the UI review gate).

### 1.7 — Local run scripts
- **Owner:** devops-agent.
- **Deliverable:** `scripts/dev.ps1` (restore + build + `dotnet watch run` on Web),
  `scripts/db-update.ps1` (apply migrations to LocalDB), a README section for local setup; a
  `/health` endpoint in Web.
- **Verify:**
  ```
  dotnet run --project src/Cinora.Web/Cinora.Web.csproj
  # then, against the running app:
  curl -i http://localhost:5000/health   # expect HTTP 200
  ```
  The dev script starts the app; `/health` returns 200; `dotnet watch` hot-reloads. (Port per
  `launchSettings.json`.)

**End-of-phase gate (Exit Criteria):** `dotnet build Cinora.sln -c Release` clean;
`dotnet test` green across all three test projects; register/sign-in (email; Google with real
creds) reaches the authenticated Home shell; `InitialCreate` builds the full schema on a clean
LocalDB; Tailwind/TS produce hashed minified assets; **zero Critical/High** findings across the
four review gates.

---

_Last reconciled against code: 2026-07-02 (Phase 1 doc pass). Corrected the drift the Phase 1 exit
review found: MediatR → hand-rolled mediator (ADR 0005) in §3/§4/§5/§7/§8/§9; option names
`OpenAIOptions`/`RedisOptions`/`BlobStorageOptions` → `AiOptions`/`CacheOptions`/`FileStorageOptions`
(free providers, ADR 0004) in §5/§6/§9; free/local-only + hand-rolled-mediator governing note and
ADRs 0004–0005 added. Verified against `Program.cs`, `Cinora.Application/Common/Messaging/*`,
`Cinora.Application/DependencyInjection.cs`, `Cinora.Infrastructure/DependencyInjection.cs`, and
`Cinora.Infrastructure/Options/*`._
