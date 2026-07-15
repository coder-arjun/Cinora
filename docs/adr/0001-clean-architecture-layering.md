# ADR 0001 — Clean Architecture Layering

- **Status:** Accepted
- **Date:** 2026-07-02
- **Phase:** 1 (Foundation)
- **Deciders:** architecture-agent (proposed), orchestrator (to ratify)

## Context

Cinora is an ASP.NET Core 10 MVC application that must remain maintainable and testable
across six phases (Foundation → Discovery → Reviews → Watchlists → AI → Polish/Deploy).
The stack pulls in framework- and vendor-coupled concerns — EF Core, ASP.NET Identity,
TMDB, OpenAI, Redis, Azure Blob, SignalR, Hangfire. Without an enforced structure these
concerns leak into business logic and make the domain untestable without a database or
network.

The team has adopted Clean Architecture with a dependency rule that points inward only.
We need to fix the exact number of projects, their names, and the precise reference
direction so that every later decision (where a class lives, which project references
which) has a single authoritative answer, and so the rule can be checked mechanically.

## Decision

Adopt a four-project production layout plus three test projects, with dependencies
pointing strictly inward.

```
tests/Cinora.Domain.Tests ─────────► src/Cinora.Domain
tests/Cinora.Application.Tests ────► src/Cinora.Application ─► src/Cinora.Domain
tests/Cinora.Web.IntegrationTests ─► src/Cinora.Web

                    ┌─────────────────────────────────────────┐
                    │            src/Cinora.Web                │  (composition root)
                    │   controllers, views, Program.cs,        │
                    │   middleware, IExceptionHandler, Serilog  │
                    └───────────────┬───────────────┬──────────┘
                       references   │               │  references
                                    ▼               ▼
              ┌─────────────────────────┐   ┌────────────────────────────┐
              │  src/Cinora.Application  │◄──│   src/Cinora.Infrastructure │
              │  CQRS, behaviors,        │   │   DbContext, EF configs,    │
              │  validators, interfaces  │   │   Identity, external adapters│
              └────────────┬────────────┘   └────────────────────────────┘
                references  │
                            ▼
              ┌─────────────────────────┐
              │     src/Cinora.Domain    │  references NOTHING (of ours)
              │  entities, value objects │
              └─────────────────────────┘
```

Exact project reference direction (the only edges allowed):

| Project | References |
|---|---|
| `Cinora.Domain` | **nothing** (no project references; ideally no NuGet either) |
| `Cinora.Application` | `Cinora.Domain` |
| `Cinora.Infrastructure` | `Cinora.Application` (and transitively Domain) |
| `Cinora.Web` | `Cinora.Application` **and** `Cinora.Infrastructure` |

`Cinora.Web` references `Cinora.Infrastructure` — a deliberately **allowed edge** — primarily to
compose the dependency graph in `Program.cs` (calling `AddInfrastructure(...)`). As a general rule,
controllers, views, and application code in Web depend on `Cinora.Application` abstractions rather
than Infrastructure implementation types.

**Sanctioned exception — the authentication vertical (see ADR 0003 and solution-structure.md §7).**
Authentication is intentionally *not* routed through the mediator: `AccountController` uses ASP.NET
Identity's `UserManager`/`SignInManager` directly, and therefore legitimately references
Infrastructure Identity types in Web — `ApplicationUser` (the Identity principal), and the
`IUserRegistrationService` / `UserRegistrationResult` seam that performs the atomic two-row account
creation (ADR 0003). This is by design, not a leak: wrapping framework Identity services in
commands would add ceremony with no value. The invariant that still holds is narrower and precise:
**`Cinora.Application`'s public surface never exposes Infrastructure types** (e.g. `IAppDbContext`
exposes only domain `DbSet<>`s, never `ApplicationUser`). So Web may touch Infrastructure Identity
types for auth; Application must not.

Bounded, deliberate exception to "Application references only Domain": `Cinora.Application`
takes a NuGet dependency on the **EF Core abstractions** package (`Microsoft.EntityFrameworkCore`)
so it can define `IAppDbContext` exposing `DbSet<T>` for read-side projections. This is a
dependency on an abstraction package, not on `Cinora.Infrastructure` and not on the SQL
Server provider. The provider (`Microsoft.EntityFrameworkCore.SqlServer`) stays in
Infrastructure. This trade-off is standard in mainstream Clean Architecture templates and
keeps query handlers terse without a repository per read.

## Consequences

**Positive**
- The dependency rule is checkable: any `ProjectReference` edge not in the table above is a
  violation and should fail review (a future architecture test can assert this).
- Domain is unit-testable with zero infrastructure; Application is testable with in-memory
  fakes of its own interfaces.
- Swapping an external provider (e.g., a different blob store) touches only Infrastructure.

**Negative / accepted costs**
- Application referencing EF Core abstractions is a pragmatic compromise purists dislike; we
  accept it and forbid the SQL Server provider and any Infrastructure type from leaking into
  Application signatures.
- Four projects plus three test projects is more ceremony than a single web project; justified
  by the six-phase roadmap and the breadth of external couplings.

## Alternatives considered

1. **Single ASP.NET Core project (no layering).** Fastest to start, but business rules would
   entangle with EF/Identity/HTTP and become untestable in isolation. Rejected — the roadmap
   is too large.
2. **Vertical-slice / feature-folder architecture without layer projects.** Attractive for
   feature cohesion, but the team's standards and skills are written around the four-layer
   model, and the dependency rule is easier to enforce with hard project boundaries. Deferred;
   feature folders are still used *within* `Cinora.Application`.
3. **Keep Application 100% EF-free (repositories/DTO abstractions for every read).** Purer, but
   adds a repository or bespoke abstraction per read with no payoff for simple projections.
   Rejected in favour of the `IAppDbContext` abstraction for reads; true repositories are
   reserved for aggregate roots with real invariants (see ADR notes and solution-structure.md).

## Related
- `docs/architecture/solution-structure.md` (authoritative Phase 1 architecture)
- ADR 0003 (ApplicationUser / Identity placement)
- ADR 0004 (free / local-only infrastructure — ports live in Application, adapters in Infrastructure)
- ADR 0005 (hand-rolled mediator — why auth bypasses the mediator)

## Amendments

- **2026-07-02 (Phase 1 doc reconciliation):** the Decision's §3 reference-direction wording was
  clarified — not reversed — to carve out the sanctioned authentication-vertical exception, so
  ADR 0001 and ADR 0003 agree. The original text implied Web must *never* touch Infrastructure
  implementation types; in fact `AccountController` legitimately uses Infrastructure Identity types
  (`ApplicationUser`, `IUserRegistrationService`, `UserRegistrationResult`) because auth uses
  `UserManager`/`SignInManager` directly rather than the mediator (ADR 0003, solution-structure.md
  §7). The load-bearing invariant is unchanged: **Application's public surface stays free of
  Infrastructure types.** The dependency-rule decision itself is untouched.

---

_Last verified against code: 2026-07-02 — reference edges per the seven `.csproj` files; the auth
carve-out verified against `src/Cinora.Web/Controllers/AccountController.cs` (injects
`SignInManager<ApplicationUser>`, `UserManager<ApplicationUser>`, `IUserRegistrationService`) and
`src/Cinora.Application/Common/Interfaces/IAppDbContext` (domain `DbSet<>`s only)._
