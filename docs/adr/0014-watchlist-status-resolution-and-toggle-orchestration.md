# ADR 0014 — Watchlist Status Resolution on Title Surfaces, and the Toggle Orchestration

- **Status:** Accepted
- **Date:** 2026-07-03
- **Phase:** 4 (Watchlists & Profile), Milestone 4.3
- **Deciders:** architecture-agent (pre-implementation design), performance-agent (consulted — N+1),
  orchestrator (ratify)

## Context

The watchlist must work from **every title surface** — the Details page, the Home/Discovery rails, and the
search results — with instant UI feedback. Two design questions must be settled once for all surfaces:

1. **How does a card get the current user's status for its title without an N+1?** A rail renders ~20 cards;
   naïvely, each card would need "is this in my watchlist, and with what status?" A per-card lazy fetch is
   20 HTTP round-trips per rail; folding a per-user status into the rail query would make the currently
   **pure, cacheable** `GetTitleRailQuery` (`ITmdbClient`-only) per-user and uncacheable.

2. **How does a write from a card obtain the internal `MovieId`?** Cards and Details address titles by
   `(media, tmdbId)`, but `Watchlist` references the internal `Movie.Id` (a `Guid`). A title must be
   persisted (have a `Movie` row) before it can be watchlisted — the same gap `CreateReviewCommand` faced
   in Phase 3 (ADR 0008 §4). The resolution must not put `ISender` inside a handler and must not persist
   every browsed card.

## Decision

**Resolve card statuses with a single batch query composed by the controller (not inside the rail query),
and orchestrate the write as `EnsureTitleCachedCommand` → `SetWatchlistStatusCommand` in the controller —
reusing the ADR-0008 pattern.**

### 1. Batch status map — one query per page, rail query stays pure

- A dedicated query
  `GetWatchlistStatusMapQuery(IReadOnlyList<int> TmdbIds, MediaType Media) : IRequest<IReadOnlyDictionary<int, WatchlistStatus>>`
  runs **one** `AsNoTracking` join:
  `Movies.Where(m => m.MediaType == Media && TmdbIds.Contains(m.TmdbId)).Join(Watchlists on Id == MovieId where UserId == me).Select(TmdbId, Status)`.
- Titles not yet first-touch persisted (most rail cards — rails never persist, ADR 0007 §4.1) simply have no
  `Movie` row and are **absent** from the map → the card renders "Add to watchlist". No row, no round-trip.
- **The controller composes it**, keeping `GetTitleRailQuery`/`SearchTitlesQuery` **`ITmdbClient`-only and
  CQRS-pure**: it resolves the rail, then (for **authenticated** users only) resolves the map, then builds a
  `WatchlistControlVm` per card into a Web view model. Anonymous requests skip the map entirely — the rail
  partial stays anonymous, cacheable, and shows a sign-in affordance instead of a status.
- Single-title surfaces (Details, watchlist page) use the scalar `GetWatchlistStatusQuery(MovieId)` /
  already-known status — no batch needed.
- Backed by **existing indexes** (Movies `(TmdbId, MediaType)` unique + Watchlists `(UserId, MovieId)`
  unique) — **no new index** for the map.

### 2. The toggle orchestration — `EnsureTitleCached` → watchlist (controller, ADR 0008)

The `WatchlistController` write actions orchestrate (never `ISender` inside a handler):
1. `var ensure = await sender.Send(new EnsureTitleCachedCommand(tmdbId, media), ct);`
2. `NotFound` → 404 (cannot watchlist a title TMDB lacks);
3. `await sender.Send(new SetWatchlistStatusCommand(ensure.MovieId!.Value, status), ct);` (or
   `GetCachedMovieIdQuery` → `RemoveFromWatchlistCommand` for remove);
4. return the re-rendered `_WatchlistControl`.

- `SetWatchlistStatusCommand` is an **upsert**: load `(currentUser, MovieId)` → `ChangeStatus` if present,
  else `Watchlist.Add`; the unique `(UserId, MovieId)` race is caught and reconciled (mirrors
  `CreateReviewCommand`). The **actor is `ICurrentUser`** (ADR 0009), never a bound id — a user can only
  touch their own `(UserId, MovieId)` row.
- On the watchlist page (title already persisted), `EnsureTitleCachedCommand` short-circuits on its
  `(TmdbId, MediaType)` existence check → one cheap indexed read, **no TMDB call** — so routing *all* surface
  writes through the same `(tmdbId, media)` endpoint is DRY and cheap. This is Phase 4's real consumer of the
  ADR-0008 seam (after Phase 3's `CreateReview`).

## Consequences

**Positive**
- **No N+1** on any title surface — one batch query per rail/search page; the rail query stays pure and
  cacheable; anonymous browse is unaffected.
- One reusable `_WatchlistControl` + one write endpoint for Details, rails, and search (DRY, instant HTMX
  feedback).
- Ownership is implicit and un-spoofable (keyed on `ICurrentUser`), so no `403` arm is needed.
- Reaffirms the ADR-0008 controller-orchestration contract with a second real consumer.

**Negative / accepted costs**
- Authenticated rail/search renders do one extra batch query (bounded, index-backed) and are per-user
  (uncacheable for that fragment) — acceptable; anonymous stays cacheable.
- Routing watchlist-page writes through `EnsureTitleCachedCommand` is a (cheap) existence check rather than a
  direct `MovieId` write — chosen for a single uniform endpoint over a micro-optimization.
- The `_TitleCard` anchor must be restructured so the control is a sibling over the poster (an interactive
  control cannot nest in the card's `<a>`) — a frontend HTML-validity task, not an architecture cost.

## Alternatives considered

1. **Per-card lazy `hx-get` for status.** Rejected: N HTTP round-trips per rail; slower and chattier than one
   batch query. Kept only as a fallback endpoint (`GET /watchlist/control`).
2. **Fold status into `GetTitleRailQuery`.** Rejected: makes the pure, cacheable rail query per-user and
   couples it to `IAppDbContext`/`ICurrentUser` — loses the ADR-0007 caching win for anonymous browse.
3. **Persist every browsed card so cards carry a `MovieId`.** Rejected (ADR 0007 §4.1): bloats the catalog
   with titles no one interacts with; only a title a user opens/acts on earns a row.
4. **Bind `MovieId` from the card.** Rejected: cards address titles by `(media, tmdbId)`; the internal `Guid`
   is server-owned and resolved via `EnsureTitleCached`/`GetCachedMovieId` (ADR 0008), not trusted from the
   client.

## Related
- ADR 0007 (catalog persistence — rails don't persist, Details first-touch does), ADR 0008 (controller
  orchestration of `EnsureTitleCachedCommand` → internal `MovieId`), ADR 0009 (`ICurrentUser` actor; no bound
  owner id).
- `docs/architecture/phase-4-watchlists-profile-design.md` §5 (watchlist control), §6 (watchlist page),
  §12 (performance).
- `src/Cinora.Web/Controllers/ReviewsController.cs` (`Create` — the ADR-0008 orchestration this mirrors);
  `src/Cinora.Application/Features/Catalog/` (`EnsureTitleCachedCommand`, `GetCachedMovieIdQuery`).
- Skills: `.claude/skills/cqrs-mediatr/SKILL.md`, `.claude/skills/ef-core-data-access/SKILL.md`,
  `.claude/skills/dotnet-performance/SKILL.md`, `.claude/skills/alpine-htmx-interactivity/SKILL.md`.

---

_Design-only ADR authored 2026-07-03 against the Phase-1/2/3 code (verified: `Watchlist.Add`/`ChangeStatus` +
unique `(UserId, MovieId)`; `EnsureTitleCachedCommand`/`GetCachedMovieIdQuery` seam; the
`ReviewsController.Create` orchestration; `GetTitleRailQuery`/`SearchTitlesQuery` are `ITmdbClient`-only and
CQRS-pure; `_TitleCard` is a single-anchor card; no watchlist verticals exist in `src/`). No application code
written._
