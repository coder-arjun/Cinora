# ADR 0003 — ApplicationUser and Identity Store Placement

- **Status:** Accepted
- **Date:** 2026-07-02
- **Phase:** 1 (Foundation)
- **Deciders:** architecture-agent (proposed), security-agent + database-agent (to implement), orchestrator (to ratify)

## Context

Cinora uses ASP.NET Identity with EF Core stores and Google OAuth. The custom Identity user
is `ApplicationUser : IdentityUser<Guid>` and requires
`AddEntityFrameworkStores<CinoraDbContext>()`. `IdentityUser<T>` lives in
`Microsoft.AspNetCore.Identity`, and the EF stores pull in
`Microsoft.AspNetCore.Identity.EntityFrameworkCore` — which depends on EF Core.

Independently, the Backend Schema lists **`User`** as one of the twelve first-class entities.
Cinora is a social platform: the friend graph (`Friend`), `Review`, `ReviewLike`, `Comment`,
`Watchlist`, `Notification`, `Device`, and `AIRecommendationHistory` all reference a user, and
the user carries genuine domain state and invariants (display name, avatar, and — in later
phases — social rules such as "cannot befriend yourself" and "no duplicate friend requests").

The dependency rule (ADR 0001) is inviolable: **`Cinora.Domain` must reference nothing of ours
and must not depend on EF Core or ASP.NET Identity.** If `ApplicationUser` (which inherits
`IdentityUser<Guid>`) were placed in Domain, Domain would transitively depend on Identity + EF
Core. That is exactly the pollution we must prevent. We must decide where the Identity user and
its stores live, and how the domain concept of a "user" is represented, without breaching the rule.

## Decision

**Split the user into two types that share one primary key.**

1. **`Cinora.Domain.Entities.User`** — a pure POCO aggregate in `Cinora.Domain`. Zero framework
   dependencies. Holds profile and social state (`DisplayName`, `AvatarUrl`, `Bio`, `CreatedAt`,
   later the friend graph and profile invariants) and exposes intent-revealing behaviour. Its key
   is a `Guid Id`. **Every other domain entity references the user by this `Guid` value** (e.g.
   `Review.UserId`), never by an Identity type.

2. **`ApplicationUser : IdentityUser<Guid>`** — lives in **`Cinora.Infrastructure`** (namespace
   `Cinora.Infrastructure.Identity`). It carries **authentication surface only** (credentials,
   security stamp, email-confirmation, external logins, lockout). It adds no social/profile
   behaviour beyond what Identity needs.

3. **Shared primary key, 1:1 relationship.** `ApplicationUser.Id` equals the corresponding
   `Domain.User.Id`. `ApplicationUser` is the Identity-created principal (created by
   `UserManager.CreateAsync`); the domain `User` row is created with the **same `Guid`** in the
   same registration transaction. The exact EF mapping (`HasOne/WithOne/HasForeignKey`, key
   sharing, delete behaviour) is **database-agent's** deliverable in Milestone 1.2; this ADR
   fixes only the placement and the shared-key contract.

4. **`CinoraDbContext` lives in `Cinora.Infrastructure`** and derives from
   `IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>`. It also exposes the domain
   `DbSet<>`s. It implements the Application-owned `IAppDbContext` interface (which exposes only
   domain `DbSet<>`s — `Users`, `Reviews`, etc.), so Application code queries domain types
   without ever seeing `ApplicationUser`.

5. **All Identity/EF coupling is confined to Infrastructure and Web:**
   - `Microsoft.AspNetCore.Identity.EntityFrameworkCore` and the EF provider are referenced by
     **Infrastructure only**.
   - `UserManager`, `SignInManager`, `AddIdentity(...).AddEntityFrameworkStores<CinoraDbContext>()`,
     and `AddGoogle(...)` are wired in **Web's `Program.cs`** (composition root), invoking an
     `AddInfrastructure(...)` extension. Account controllers in Web use `UserManager`/`SignInManager`
     directly for auth flows (see the CQRS policy in solution-structure.md — auth is not routed
     through the mediator; and ADR 0005, why the hand-rolled mediator is bypassed here). This is the
     sanctioned Web→Infrastructure Identity-type edge that ADR 0001 §3 carves out.

Net effect: `Cinora.Domain` depends on nothing; the Identity user and its EF stores are pure
Infrastructure; the domain "user" concept is a first-class, behaviour-bearing Domain aggregate.

## Consequences

**Positive**
- Domain purity is preserved — no EF Core / Identity leakage inward.
- The social domain gets a real `User` aggregate to hang invariants on (Phases 2–5), instead of
  scattering user rules across handlers or onto an Infrastructure Identity type.
- `IAppDbContext` can expose `DbSet<User>` (a domain type), keeping Application free of
  `ApplicationUser`.
- Auth concerns evolve independently of profile/social concerns.

**Negative / accepted costs**
- Two rows per user (`AspNetUsers` + domain `Users`) that must be created together. Mitigation:
  registration is a single transactional flow (create `ApplicationUser`, then domain `User` with
  the same `Id`); a failure rolls back both. database-agent owns making this atomic.
- A small risk of drift between the two records (e.g. email/displayname). Mitigation: keep
  authoritative ownership explicit — email/credentials on `ApplicationUser`; profile/social on
  `Domain.User`; do not duplicate fields across both without a documented reason.
- Slightly more mapping code than a single-table approach.

## Alternatives considered

1. **`ApplicationUser` in Domain.** Simplest conceptually, but forces `Cinora.Domain` to
   reference ASP.NET Identity + EF Core. **Rejected — violates the dependency rule (ADR 0001).**
   This is the specific pollution this ADR exists to prevent.

2. **`ApplicationUser` in Infrastructure as the *only* user record; no domain `User` type.**
   (The common jasontaylordev approach.) Domain entities store a bare `Guid UserId` with no
   navigation. Leaner (one table, no sync). **Rejected for Cinora** because it leaves the social
   domain with nowhere to put user invariants: `Friend`/profile rules would live on an
   Infrastructure Identity type (wrong layer) or in handlers (anemic domain). For a
   social-first product where `User` is a central aggregate, the split pays off. This remains the
   fallback if the two-table cost proves not worth it.

3. **A separate `IdentityDbContext` distinct from the domain `CinoraDbContext`.** Cleaner
   separation of migrations, but two contexts over one database complicates transactions spanning
   auth + domain (exactly what registration needs) and duplicates configuration. **Rejected** —
   one `CinoraDbContext` deriving from `IdentityDbContext<...>` is simpler and keeps registration
   atomic.

4. **Map profile fields onto `ApplicationUser` and expose it through an interface to Application.**
   Would still require Application to reference the Identity type or a shim, and blurs auth vs
   domain state. **Rejected** in favour of the clean split.

## Related
- ADR 0001 (layering / dependency rule — its §3 carves out the auth-vertical Web→Infrastructure edge)
- ADR 0002 (LocalDB — where the shared schema is created)
- ADR 0005 (hand-rolled mediator — why auth is not routed through the mediator)
- `docs/architecture/solution-structure.md` (§ Where each concern lives, § IAppDbContext, Milestones 1.2–1.3)
- Skill: `.claude/skills/aspnet-identity-google-oauth/SKILL.md`

## Amendments

- **2026-07-02 (Phase 1 doc reconciliation):** Decision point 5's passing "not forced through
  MediatR" was corrected to "not routed through the mediator" — MediatR was replaced by a
  hand-rolled mediator (ADR 0005) and is no longer referenced. Added the explicit note that this
  auth flow is the sanctioned Web→Infrastructure Identity-type edge now carved out in ADR 0001 §3.
  The placement decision (two-type shared-PK split) is unchanged.

---

_Last verified against code: 2026-07-02 — `src/Cinora.Infrastructure/Identity/{ApplicationUser,
IUserRegistrationService,UserRegistrationResult,UserRegistrationService}.cs`,
`src/Cinora.Infrastructure/Persistence/CinoraDbContext.cs` (derives from
`IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>`, implements `IAppDbContext`), and
`src/Cinora.Web/Controllers/AccountController.cs` (uses `UserManager`/`SignInManager` +
`IUserRegistrationService` directly)._
