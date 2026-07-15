# ADR 0009 — Resource Ownership Enforced in Handlers, and the Current-User Seam

- **Status:** Accepted
- **Date:** 2026-07-03
- **Phase:** 3 (Reviews & Social), applies to every social write (Milestones 3.1–3.5)
- **Deciders:** architecture-agent (pre-implementation design), security-agent (consulted), orchestrator (ratify)

## Context

Phase 3 introduces the first **authenticated write** use cases in Cinora: create/edit/delete a review,
like/comment, send/accept/remove a friend, mark a notification read. Two questions must be answered once,
consistently, for all of them:

1. **Where does the acting user's identity come from?** Phase 1 authentication used
   `UserManager`/`SignInManager` directly in `AccountController` and never needed the current user's `Guid`
   inside a handler. Phase 2 was anonymous browse only. No `ICurrentUser` seam exists in the codebase yet
   (it is foreshadowed in `solution-structure.md` §5 but was never built — YAGNI held through Phase 2).
   Phase 3 handlers must know *who is acting* to stamp `Review.UserId`, `Comment.UserId`,
   `Friend.RequesterId`, etc.

2. **How is "you may edit/delete only your own review/comment" enforced?** This is a first-class Phase-3
   requirement and a standing REVIEW_BACKLOG contract. The `security-hardening` skill names two mechanisms:
   an ASP.NET Core **resource-based `IAuthorizationHandler`** (policy evaluated in the controller against a
   loaded resource) versus an **ownership check in the write path**. The wrong split leaks the authorization
   decision out of the transaction where it matters, or forces the resource to be loaded twice.

The tension is a Clean Architecture one. A resource-based `IAuthorizationHandler` lives in **Web** (it
depends on `Microsoft.AspNetCore.Authorization` primitives and needs the controller to pre-load the entity);
the actual mutation happens in an **Application** command handler inside the unit of work. If ownership is
checked only in Web, the authoritative security boundary sits *outside* the transaction and *outside* the
layer that owns the invariant, and it cannot be unit-tested without the full MVC pipeline.

## Decision

**Resolve the acting identity through a new `ICurrentUser` Application port, and enforce resource ownership
inside the Application command handler — never from a client-supplied field, and never solely in Web.**

### 1. The current-user seam — `ICurrentUser` (Application port, Infrastructure adapter)

- `Cinora.Application/Common/Interfaces/ICurrentUser.cs`:
  ```csharp
  public interface ICurrentUser
  {
      Guid? UserId { get; }            // null when anonymous
      bool IsAuthenticated { get; }
      Guid GetRequiredUserId();        // throws if anonymous — used by [Authorize]-only handlers
  }
  ```
- Implemented in `Cinora.Infrastructure/Identity/CurrentUser.cs`, reading `IHttpContextAccessor` →
  `ClaimTypes.NameIdentifier` (the same claim SignalR's `Context.UserIdentifier` uses, keeping the group-per-user
  identity consistent — ADR 0011). Registered in `AddInfrastructure` together with `AddHttpContextAccessor()`.
- **The acting `UserId` is NEVER a bound field on a command.** Commands carry *resource* ids (a route
  `ReviewId`, a `MovieId`, an `AddresseeUserId`) but resolve the *actor* from `ICurrentUser`. A hidden
  `UserId`/`AuthorId` form field is a spoofing vector (the skill's "never trust a hidden UserId" rule) and is
  banned. This keeps handlers unit-testable (fake `ICurrentUser`) and closes the tampering surface.

### 2. Ownership enforced in the handler, surfaced as 403

- A mutation of an owned resource (edit/delete a review, delete a comment, respond to a friend request) loads
  the tracked entity, then **before calling the domain method** compares the entity's owner to
  `ICurrentUser.GetRequiredUserId()`. On mismatch it throws a new
  `Cinora.Application/Common/Exceptions/ForbiddenAccessException`.
- `GlobalExceptionHandler` (Web) gains one mapping: `ForbiddenAccessException` → **403 ProblemDetails**
  (alongside the existing `ValidationException`→400 / `NotFoundException`→404 / `DomainException`→400 / else
  500). A missing resource is `NotFoundException`→404; an existing resource you do not own is 403. Reviews are
  public (rendered on public title pages), so distinguishing 403 from 404 leaks nothing.
- The ownership check is the **authoritative boundary**: it runs *inside* the same unit of work as the write,
  in the layer that owns the invariant, and is proven by an Application unit test without HTTP.

### 3. Resource-based `IAuthorizationHandler` is optional presentation sugar only

- A resource-based policy (e.g. `CanModifyReview` with a `ReviewOwnerRequirement`) MAY be used in Web to gate
  the **GET edit-form** action and to hide/show edit/delete controls in the view — improving UX and returning
  a clean 403 before rendering. It is **never the sole gate**: the POST/PUT/DELETE always re-checks ownership
  in the handler, because view-level gating races and can be bypassed by a direct request.
- Because handler-side enforcement is authoritative, the resource-based handler is optional for Phase 3 and
  may be added per feature where it improves UX; it does not become required plumbing.

## Consequences

**Positive**
- One consistent, testable authorization story for all social writes; the security boundary sits at the
  write, in the transaction, in the Application layer that owns the invariant.
- No client-trusted identity anywhere; the tampering surface (hidden owner fields) does not exist.
- `ICurrentUser` is the small, long-lived seam Phases 3–5 all reuse (watchlists, AI history, profile edits).
- Handlers stay unit-testable with a faked `ICurrentUser`; no `WebApplicationFactory` needed to prove authZ.

**Negative / accepted costs**
- One new Application port + Infrastructure adapter + `AddHttpContextAccessor()` registration.
- One new Application exception and one new `GlobalExceptionHandler` arm (403). Small, additive.
- Ownership logic lives in handlers rather than a single declarative policy; mitigated by a shared private
  guard helper and by the pattern being identical across handlers (load → compare-to-`ICurrentUser` → throw
  `ForbiddenAccessException`).

## Alternatives considered

1. **Resource-based `IAuthorizationHandler` in Web as the sole mechanism.** Rejected as *sole* boundary: it
   forces the controller to pre-load the entity (a duplicate load), moves the authZ decision out of the
   transaction and out of the Application layer, and cannot be unit-tested at the Application boundary. Kept
   as *optional* view sugar (Decision §3).
2. **Bind `UserId` into the command from a hidden field / route.** Rejected: spoofable; violates the
   never-trust-client rule. The actor is always server-resolved.
3. **A domain-service / aggregate-root repository that encapsulates ownership.** Rejected for Phase 3 (YAGNI,
   §7 no-repository stance): the load-compare-mutate pattern on `IAppDbContext` is sufficient and consistent
   with ADR 0008. Revisit if `Review` acquires enough invariants to become a true aggregate root with a
   repository.

## Related
- ADR 0001 (layering — ports in Application, adapters in Infrastructure), ADR 0003 (users referenced by
  `Guid`, `ApplicationUser` never crosses into Application), ADR 0008 (controller orchestration of commands),
  ADR 0011 (SignalR `Context.UserIdentifier` uses the same `NameIdentifier` claim as `ICurrentUser`).
- `docs/architecture/phase-3-social-design.md` §2 (authorization model), §3 (XSS stance).
- `docs/architecture/solution-structure.md` §5 (`ICurrentUser` foreshadowed), §7 (CQRS boundary, no
  generic repository).
- REVIEW_BACKLOG "Global auth contracts" (fail-closed authZ; opt-out anti-forgery).
- Skills: `.claude/skills/security-hardening/SKILL.md`, `.claude/skills/cqrs-mediatr/SKILL.md`,
  `.claude/skills/clean-architecture-dotnet/SKILL.md`.

---

_Design-only ADR authored 2026-07-03 against the Phase-1/2 code (verified: no `ICurrentUser` type exists in
`src/`; `GlobalExceptionHandler` maps `ValidationException`/`NotFoundException`/`DomainException`;
`Review`/`Comment`/`Friend` carry `UserId`/`RequesterId`/`AddresseeId` and expose owner-mutating domain
methods). No application code written._
