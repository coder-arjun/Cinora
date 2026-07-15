# ADR 0002 — Local Development Database: SQL Server LocalDB, No Docker

- **Status:** Accepted
- **Date:** 2026-07-02
- **Phase:** 1 (Foundation)
- **Deciders:** architecture-agent (proposed), devops-agent (to implement), orchestrator (to ratify)

## Context

Phase 1 must produce a runnable skeleton whose initial EF Core migration creates the full
schema on a clean SQL Server database, and whose tests run against real SQL Server semantics.

Verified constraints on the current development machine (see `PROGRESS.md`, verified 2026-07-02):

- .NET SDK 10.0.301, ASP.NET Core 10 runtime, EF Core tools `dotnet-ef` 10.0.8.
- **SQL Server LocalDB** is installed and available at `(localdb)\MSSQLLocalDB`; SQL Express
  service is also running.
- **Docker is NOT installed.** Therefore Testcontainers-based SQL Server and any
  container-based Redis are unavailable locally.
- Redis is not needed until Phase 2 (caching); Cinora is designed to degrade gracefully
  without it.

The original phase text mentioned "SQL Server + Redis (containers or local)". With Docker
absent we must pick the "local" path and record it so no downstream agent wastes effort on
containers.

## Decision

1. **Default local database is SQL Server LocalDB** at `(localdb)\MSSQLLocalDB`. The
   development connection string uses the Cinora-specific database name **`CinoraDev`**, as
   implemented in `appsettings.Development.json` (`ConnectionStrings:DefaultConnection`):

   ```
   Server=(localdb)\MSSQLLocalDB;Database=CinoraDev;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True
   ```

2. **Integration tests target LocalDB**, not Testcontainers, using the dedicated database name
   **`CinoraTest`** (implemented in the integration-test `WebApplicationFactory`, kept separate from
   the developer's `CinoraDev`) created and reset by the test host. Rationale: Docker is
   unavailable, and LocalDB gives real SQL Server behaviour (indexes, unique constraints,
   cascade rules) that an in-memory provider cannot reproduce. If Docker becomes available
   later, Testcontainers can be adopted without changing production code.

3. **Redis is optional locally with graceful degradation.** Phase 1 defines `CacheOptions`
   (whose `Provider` defaults to the free in-memory `"Memory"` backend; a Redis-compatible
   server such as Memurai is opt-in per ADR 0004) but does not require a Redis connection to
   boot or run. When no Redis endpoint is configured, caching falls back to the built-in
   in-memory `IDistributedCache`. A hard Redis dependency is not introduced before Phase 2.

4. **Connection-string strategy per environment:**
   - **Development:** non-secret LocalDB connection string in `appsettings.Development.json`
     (LocalDB uses Windows integrated auth — no password to protect). Any real secret (e.g.
     a future SQL login password) goes in **user-secrets**, never in `appsettings*.json`.
   - **Test:** LocalDB connection string supplied by the integration-test host (config
     override / environment variable), pointing at the dedicated test database.
   - **Production (Phase 6):** Azure SQL connection string and all secrets come from
     environment variables / Azure App Configuration / Key Vault (`ConnectionStrings__Default`),
     never from committed files.

## Consequences

**Positive**
- Zero external prerequisites for a new developer beyond the .NET SDK + LocalDB (already
  present with Visual Studio / SQL Server Express tooling).
- Migrations and integration tests exercise genuine SQL Server behaviour.
- The Docker-absent constraint is recorded once; agents will not attempt container setup in
  Phase 1.

**Negative / accepted costs**
- LocalDB is Windows-only; CI on Linux (Phase 6) will need a real SQL Server container or an
  Azure SQL instance. This is acceptable because it is a CI concern deferred to deployment,
  and the connection-string strategy already isolates the environment difference.
- Tests share a SQL Server instance, so the test host must reset state deterministically
  (respawn / delete-and-recreate) to stay isolated.

## Alternatives considered

1. **Testcontainers + Dockerized SQL Server / Redis.** The team's default for realistic
   integration tests, but **Docker is not installed** — not viable in Phase 1. Revisit if
   Docker is added; no production code change required.
2. **EF Core In-Memory provider for tests.** No external dependency, fast, but it does not
   enforce unique indexes, relational constraints, or cascade behaviour that Cinora's schema
   relies on (e.g. one-review-per-user-per-movie). Rejected for integration tests; may be
   used only for narrow Application unit tests where relational fidelity is irrelevant.
3. **SQL Express as the primary local DB.** Available, but LocalDB is lighter, developer-owned,
   and the conventional choice for local ASP.NET development. SQL Express remains a fallback.
4. **Require Redis locally.** Rejected — unnecessary before Phase 2 and would block boot on a
   machine without Redis. Graceful degradation is the design.

## Related
- `docs/architecture/solution-structure.md` (§ Options, § Milestone 1.2 and 1.7)
- ADR 0001 (layering — Infrastructure owns the provider and connection)
- ADR 0004 (free / local-only infrastructure — LocalDB is the SQL Server substitution)

## Amendments

- **2026-07-02 (Phase 1 doc reconciliation, closes backlog A5):** the example database names were
  corrected to match the shipped code — dev database `Cinora` → **`CinoraDev`** (per
  `appsettings.Development.json`), integration-test database `Cinora_IntegrationTests` →
  **`CinoraTest`** (per the integration-test `WebApplicationFactory`), and the example string's
  `Encrypt=False` aligned to the implemented `TrustServerCertificate=True`. Point 3's stale
  `RedisOptions` was also corrected to the shipped `CacheOptions` (in-memory default, ADR 0004).
  The decision (LocalDB, no Docker, per-environment isolation) is unchanged.

---

_Last verified against code: 2026-07-02 — `src/Cinora.Web/appsettings.Development.json`
(`Database=CinoraDev`) and `tests/Cinora.Web.IntegrationTests/Infrastructure/CinoraWebApplicationFactory.cs`
(`Database=CinoraTest`)._
