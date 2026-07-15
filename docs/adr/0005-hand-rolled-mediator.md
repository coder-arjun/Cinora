# ADR 0005 — Hand-Rolled Minimal Mediator (No MediatR)

- **Status:** Accepted
- **Date:** 2026-07-02
- **Phase:** 1 (Foundation), Milestone 1.4
- **Deciders:** user (free-only constraint owner), backend-agent (to implement), orchestrator (to ratify)

## Context

The Application layer uses a CQRS request/handler shape with cross-cutting pipeline behaviors
(logging, validation, performance). The mainstream .NET library for this is **MediatR**, and the
original design named it (`solution-structure.md` §4 package list, §7 CQRS policy).

`solution-structure.md` §8 flagged a risk: *"Recent MediatR versions moved toward a commercial
license."* That risk has been **realized** — MediatR now ships under a commercial license. Under
the free-only, no-subscription constraint (ADR 0004), taking a dependency that can require a paid
license is not acceptable. `FluentValidation` (Apache-2.0) and `Serilog` (Apache-2.0) remain free
and are kept; MediatR must go.

The pipeline surface Cinora actually uses is small: send a request, resolve its single handler,
wrap it in an ordered set of behaviors. That is roughly one file — small enough to own outright.

## Decision

**Implement a hand-rolled minimal mediator in `Cinora.Application/Common/Messaging`**, matching the
`cqrs-mediatr` skill's patterns, and take **no mediator NuGet dependency**.

Public contracts (in `Cinora.Application/Common/Messaging`):

- **`IRequest<TResponse>`** (`IRequest.cs`) — marker for a command/query producing `TResponse`.
- **`IRequestHandler<TRequest, TResponse>`** (`IRequestHandler.cs`) — `Task<TResponse> Handle(TRequest, CancellationToken)`;
  exactly one implementation per request type.
- **`ISender`** (`ISender.cs`) — the dispatch entry point injected by controllers/hubs/jobs:
  `Task<TResponse> Send<TResponse>(IRequest<TResponse>, CancellationToken)`.
- **`IPipelineBehavior<TRequest, TResponse>`** + **`RequestHandlerDelegate<TResponse>`** (`IPipelineBehavior.cs`) —
  the onion around the handler; behaviors run outermost-first in registration order.
- **`Sender`** (`Sender.cs`) — the concrete dispatcher. It caches, per concrete request type, a typed
  wrapper (`RequestHandlerWrapperImpl<TRequest,TResponse>` behind `RequestHandlerWrapper<TResponse>`)
  in a `ConcurrentDictionary`, so the one-time reflection (`MakeGenericType` + `Activator.CreateInstance`)
  is paid once per request type; subsequent sends hit the cache. The wrapper resolves the handler and
  the ordered behaviors from the request scope and folds the behaviors around the handler.

Registration (`Cinora.Application/DependencyInjection.cs`, `AddApplication()`):

- `ISender` → `Sender`, **scoped** (its captured `IServiceProvider` is the request scope, so it can
  resolve scoped handlers that depend on the scoped `IAppDbContext`).
- Every `IRequestHandler<,>` in the Application assembly is discovered and registered **transient**;
  a **second** implementation for the same closed handler interface throws
  `InvalidOperationException` at scan time (fail-loud on duplicate handlers) rather than silently
  winning last.
- FluentValidation validators via `AddValidatorsFromAssembly(..., includeInternalTypes: true)`.
- Behaviors as open generics, in execution order — **Logging → Validation → Performance** —
  i.e. `LoggingBehavior<,>` (outermost) wraps `ValidationBehavior<,>` wraps `PerformanceBehavior<,>`
  wraps the handler. `ValidationBehavior` no-ops when a request has no validator.

Phase 1 ships **zero requests**; the pipeline is scaffolded so Phase 2 features slot in with no
plumbing. The whole thing sits behind `AddApplication()`, so swapping the implementation later is a
one-call change.

## Consequences

**Positive**
- **Zero third-party / licensing exposure** for the mediator — nothing that can turn commercial the
  way MediatR did. Consistent with the free-only constraint (ADR 0004).
- Tiny, owned surface (~a handful of small files) matching the skill's patterns; easy to read and test.
- Swappable behind `AddApplication()`; controllers depend only on `ISender`.

**Negative / accepted costs**
- **No streaming and no notifications/publish** are implemented — only single request→single response.
  If a fan-out/notification use case appears, add `INotification`/`IPublisher` (or reconsider a library) then.
- **One small reflection cost per request type** on first dispatch (`MakeGenericType` +
  `Activator.CreateInstance`); **cached** thereafter, so hot paths pay nothing extra.
- We own correctness: duplicate-handler detection, behavior ordering, and scope lifetimes are on us
  (covered by Application/integration tests and the fail-loud duplicate-handler guard).

## Alternatives considered

1. **MediatR (the original design).** Mature and idiomatic, but now **commercial-licensed** —
   disallowed by the free-only constraint (ADR 0004). **Rejected.**
2. **Pin the last permissively-licensed MediatR version.** Free today, but frozen: no fixes/updates,
   and an odd long-term dependency to explain. **Rejected** in favour of owning the tiny surface.
3. **`martinothamar/Mediator` (MIT, source-generated).** Genuinely free and fast, but still an
   external dependency (and a different registration/codegen model). **Rejected** — adds a dependency
   for a surface we can own in ~one file.
4. **Hand-rolled minimal mediator (chosen).** Zero third-party/licensing exposure, ~one file matching
   the `cqrs-mediatr` skill, and swappable behind `AddApplication()`. Cost: no streaming/notifications
   yet and a cached one-time reflection per request type — both acceptable.

## Related
- ADR 0004 (free-only infrastructure — the constraint that forbids a commercial dependency)
- `docs/architecture/solution-structure.md` (§4 packages, §5 concern map, §7 CQRS policy, §8 risks)
- Skill: `.claude/skills/cqrs-mediatr/SKILL.md` (the request/handler/behavior patterns matched)
- Code: `src/Cinora.Application/Common/Messaging/{IRequest,IRequestHandler,ISender,IPipelineBehavior,Sender}.cs`;
  `src/Cinora.Application/DependencyInjection.cs`; behaviors in `src/Cinora.Application/Common/Behaviors/`

---

_Last verified against code: 2026-07-02 — `Sender.cs` (cached typed-wrapper dispatch), `DependencyInjection.cs` (`ISender` scoped, transient handlers, duplicate-handler throw, behaviors Logging→Validation→Performance). No MediatR package in `Directory.Packages.props` or any `.csproj`._
