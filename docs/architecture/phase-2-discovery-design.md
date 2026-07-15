# Cinora — Phase 2 Discovery Solution Design (Authoritative)

- **Status:** Accepted for Phase 2 (Discovery) — pre-implementation design review
- **Date:** 2026-07-02
- **Owner:** architecture-agent
- **Builds on:** [Phase 1 solution structure](solution-structure.md) (hand-rolled `ISender`, free-named
  options, LocalDB, fail-closed authZ + anti-forgery + strict CSP)
- **New ADRs:** [0006 TMDB integration boundary](../adr/0006-tmdb-integration-boundary.md),
  [0007 TMDB caching & catalog persistence](../adr/0007-tmdb-caching-and-catalog-persistence.md)
- **Reconciling ADR (Phase-2 exit):** [0008 TMDB catalog persistence via `ISender` commands](../adr/0008-tmdb-persistence-via-commands.md)
- **Governing ADRs:** [0001 Layering](../adr/0001-clean-architecture-layering.md),
  [0004 Free/local-only](../adr/0004-free-local-only-infrastructure.md),
  [0005 Hand-rolled mediator](../adr/0005-hand-rolled-mediator.md)

This document is the single source of truth for Phase 2 (Discovery). It designs the TMDB integration,
caching, catalog persistence, the `ISender` feature verticals, the UI, and performance stance, then
gives a delegable milestone build order. Implementation agents follow it; deviations require an ADR.

> **Design-only.** No code was written and no build/test was run producing this document. Every
> `Verify` command in §9 is an acceptance check the *implementing* agent must run and show output for.

> **Reconciliation note (2026-07-02, Phase-2 exit-layer review) — persistence seam.** As authored, this
> design named an **`ITitleCatalog`** Infrastructure port (`EnsureTitleAsync` / `SyncGenresAsync`) as the
> catalog-persistence seam (§1 component table, §2.1 diagram, §5 feature table, §9.2 milestone). **That
> port was never built.** The shipped code realizes the seam as two `ISender` commands —
> **`SyncGenresCommand`** and **`EnsureTitleCachedCommand`** (in
> `src/Cinora.Application/Features/Catalog/`), whose handlers persist via **`IAppDbContext` directly** —
> per **[ADR 0008](../adr/0008-tmdb-persistence-via-commands.md)**, which defers the port and defines the
> Phase-3 trigger to extract it. The §1, §2.1, §5, and §9.2 references below have been reconciled inline
> to the commands. Phase-3/4 reuse is via **controller orchestration** (dispatch
> `EnsureTitleCachedCommand` → read `MovieId` → dispatch the next command), **not** a handler calling a
> port internally.

> **Governing constraints (unchanged from Phase 1, restated because Phase 2 is the first to actually
> consume external infra):**
> - **Free / local-only (ADR 0004).** TMDB free tier; **in-memory `IDistributedCache`** (NOT Redis,
>   NOT Azure); no Docker; no paid services. The cache/AI/storage *ports* stay so a paid provider
>   could be swapped later, but only the free adapter ships.
> - **Hand-rolled mediator (ADR 0005).** Every "mediator/`ISender`" reference is the in-house type in
>   `Cinora.Application/Common/Messaging`. **Never add the MediatR package.**
> - **Fail-closed contracts (Phase 1 §1.3/§1.6b, REVIEW_BACKLOG).** Global fallback authZ (every
>   endpoint requires auth unless `[AllowAnonymous]`); global `AutoValidateAntiforgeryToken`; strict
>   `default-src 'self'` CSP. Phase 2 must honor and make three explicit, pre-recorded extensions to
>   these (§6).

---

## 1. What Phase 2 adds and where every concern lives

Phase 2 introduces the **first real `ISender` use cases** and the **first external adapter**
(`ITmdbClient`). The layering from Phase 1 is unchanged; the dependency rule is inviolable (ADR 0001).

| Concern | Layer / location | Notes |
|---|---|---|
| **`ITmdbClient` port** (trending/popular/top-rated, search, details+credits, genre lists) | `Cinora.Application/Common/Interfaces/ITmdbClient.cs` | Returns **Application read models**, never TMDB DTOs, never Domain entities (ADR 0006). |
| **TMDB read models** — `TmdbTitleSummary`, `TmdbTitleDetails`, `TmdbCastMember`, `TmdbGenre`, `TmdbPage<T>` | `Cinora.Application/Common/Tmdb/` | Cinora-owned record shapes. May use the Domain `MediaType` enum (Application→Domain is allowed). |
| **`ITmdbImageUrlBuilder` port** | `Cinora.Application/Common/Interfaces/ITmdbImageUrlBuilder.cs` | `Build(path, TmdbImageSize)`. Impl reads `TmdbOptions.ImageBaseUrl`. |
| **Catalog persistence commands** — `SyncGenresCommand`, `EnsureTitleCachedCommand` | `Cinora.Application/Features/Catalog/` | The canonical TMDB→internal-`Guid` persistence seam (ADR 0007); handlers persist via `IAppDbContext` directly — realized as commands, not a port (ADR 0008; see the reconciliation note above). |
| **`TmdbClient` adapter + TMDB DTOs + mapper** | `Cinora.Infrastructure/Tmdb/` (`TmdbClient.cs`, `Dtos/*`, `TmdbMapper.cs`) | `HttpClient` + resilience. DTOs are `internal`, snake_case `[JsonPropertyName]`, and **never leave Infrastructure**. |
| **`CachedTmdbClient` decorator** | `Cinora.Infrastructure/Tmdb/CachedTmdbClient.cs` | Cache-aside over `IDistributedCache`; wraps the raw `TmdbClient` (ADR 0007). |
| ~~`TitleCatalog` adapter~~ — **not built** (ADR 0008) | — | Superseded by the two command handlers above; each upserts `Movie`/`Genre`/`MovieGenre` by the `(TmdbId, MediaType)` / `TmdbGenreId` unique indexes via `IAppDbContext`. |
| **`TmdbImageUrlBuilder`** | `Cinora.Infrastructure/Tmdb/TmdbImageUrlBuilder.cs` | Reads `IOptions<TmdbOptions>`. |
| **Feature verticals** — `GetHomeFeedQuery`, `SearchTitlesQuery`, `GetTitleDetailsQuery` (Discovery); `EnsureTitleCachedCommand`, `SyncGenresCommand` (Catalog) | `Cinora.Application/Features/Discovery/` and `Cinora.Application/Features/Catalog/` | Request + handler + validator + view-model DTO colocated per feature. (The two catalog commands ship in `Features/Catalog/`; the Discovery queries are Phase-2 milestones still to be built.) |
| **`DiscoveryController` / `HomeController` browse actions, rail & result partials, details view** | `Cinora.Web/Controllers` + `Views/Discovery` | Inject `ISender`; return full views or HTMX partials. All browse actions `[AllowAnonymous]` (§6). |
| **TMDB image tag-helper** (`<img asp-tmdb-poster ...>`) | `Cinora.Web/TagHelpers/TmdbImageTagHelper.cs` | Backed by `ITmdbImageUrlBuilder`; chooses size per context. |
| **DI wiring** — `AddHttpClient<>()` + resilience, decorator, `IDistributedCache`, ports | `Cinora.Infrastructure/DependencyInjection.cs` (`AddInfrastructure`) | `TmdbOptions.ValidateOnStart()` switched **ON** here (§2). `AddDistributedMemoryCache()`. |

**Anti-patterns still banned (Phase 1 §5, reaffirmed):** no generic `IRepository<T>`; no business rules
in handlers/behaviors; no `IConfiguration` injected into services (bind Options); **no external-API DTO
ever crosses into `Cinora.Domain`** — Phase 2 is the first real test of this rule, enforced by ADR 0006.

---

## 2. TMDB integration

### 2.1 Port / adapter split (ADR 0006)

- **Port in Application, adapter in Infrastructure.** `ITmdbClient` is defined in Application so
  handlers depend only on the abstraction. `TmdbClient` (the HTTP adapter) lives in Infrastructure.
- **The port returns Application read models, not DTOs, not Domain entities.** The data flow is a
  strict three-hop map that keeps each type in exactly one layer:

  ```
  TMDB JSON
    → TmdbMovieDto / TmdbSearchDto / ...   (Infrastructure, internal, snake_case [JsonPropertyName])
    → TmdbTitleSummary / TmdbTitleDetails  (Application read models — returned by ITmdbClient)
    → Movie.FromTmdb(...) / Genre.Create() (Domain — persistence path only; the two catalog commands, ADR 0008)
  ```

  Rationale: display rails/search must not construct or persist Domain `Movie` aggregates (each would
  mint a new `Guid` + `CachedAtUtc`), and TMDB's snake_case DTOs must not leak past the adapter. The
  read models are the contract; the Domain entity is touched only when we deliberately persist (§4).

### 2.2 Registration & resilience

```csharp
// Cinora.Infrastructure/DependencyInjection.cs (AddInfrastructure) — sketch, not literal
services.AddOptions<TmdbOptions>()
    .BindConfiguration(TmdbOptions.SectionName)
    .ValidateUsingDataAnnotations()
    .ValidateOnStart();                 // NEW in Phase 2 — TMDB is now consumed (REVIEW_BACKLOG gate)

services.AddDistributedMemoryCache();   // FREE default — not Redis, not Azure (ADR 0004)

// Raw HTTP + mapping client registered against the CONCRETE type so the cache decorator can wrap it.
services.AddHttpClient<TmdbClient>((sp, http) =>
{
    var o = sp.GetRequiredService<IOptions<TmdbOptions>>().Value;
    http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);
})
.AddStandardResilienceHandler();        // Microsoft.Extensions.Http.Resilience (see 2.3)

// ITmdbClient resolves to the cache-aside decorator wrapping the raw client (manual decoration —
// no Scrutor needed; the free set has none).
services.AddScoped<ITmdbClient>(sp => new CachedTmdbClient(
    sp.GetRequiredService<TmdbClient>(),
    sp.GetRequiredService<IDistributedCache>(),
    sp.GetRequiredService<IOptions<CacheOptions>>(),
    sp.GetRequiredService<ILogger<CachedTmdbClient>>()));

// Catalog persistence ships as SyncGenresCommand + EnsureTitleCachedCommand (auto-discovered as
// IRequestHandlers by AddApplication, ADR 0008) — no dedicated persistence port is registered.
services.AddSingleton<ITmdbImageUrlBuilder, TmdbImageUrlBuilder>();
```

- **Resilience: use `Microsoft.Extensions.Http.Resilience` (`AddStandardResilienceHandler()`), NOT
  hand-rolled Polly.** Why: it is **free (MIT), first-party**, and ships the exact pipeline a flaky
  third-party API needs in one call — rate limiter, total-request timeout, retry with exponential
  backoff + jitter that **honors `Retry-After` on 429**, circuit breaker, per-attempt timeout. It is
  built on Polly v8 internally, so we get Polly's engine without owning policy wiring or adding a
  second resilience library. (Raw Polly remains available for a bespoke policy later, but the standard
  handler is the right default and avoids reinventing it.)
- **Auth.** `TmdbOptions.ApiKey` is sent as a **v4 Bearer token** in the `Authorization` header (keeps
  the credential out of URLs and logs — the skill's rule). The user should supply a TMDB **v4 Read
  Access Token** in user-secrets (`Tmdb:ApiKey`). If only a v3 key is available, the adapter may
  instead append `?api_key=` — but Bearer is the recommended, log-safe path. **This is the one item
  that needs the user's free TMDB key** (see §9 sequencing).
- **`ValidateOnStart` now ON.** Since TMDB is consumed this phase, a missing/empty `Tmdb:ApiKey` must
  fail fast at boot (REVIEW_BACKLOG gate). This is intentional fail-closed behavior. **Testing** and
  the mock-first milestones satisfy it with a **dummy non-empty key string** in configuration (the
  data-annotation only checks non-empty); a **fake `ITmdbClient`** replaces the network, so no live
  call is made (§9).

### 2.3 `ITmdbClient` surface (Application)

```csharp
public interface ITmdbClient
{
    Task<IReadOnlyList<TmdbTitleSummary>> GetTrendingAsync(MediaType media, CancellationToken ct);
    Task<IReadOnlyList<TmdbTitleSummary>> GetPopularAsync(MediaType media, CancellationToken ct);
    Task<IReadOnlyList<TmdbTitleSummary>> GetTopRatedAsync(MediaType media, CancellationToken ct);
    Task<TmdbPage<TmdbTitleSummary>> SearchAsync(MediaType media, string query, int page, CancellationToken ct);
    Task<TmdbTitleDetails?> GetDetailsAsync(MediaType media, int tmdbId, CancellationToken ct); // ?append_to_response=credits
    Task<IReadOnlyList<TmdbGenre>> GetGenresAsync(MediaType media, CancellationToken ct);
}
```

Every method takes a `CancellationToken` (bound by MVC from `HttpContext.RequestAborted`) and returns
Cinora shapes. `GetDetailsAsync` returns `null` on 404 (unknown title) — the decorator must **not**
cache the null (§3). Details is fetched with `append_to_response=credits` so cast comes back in one call.

### 2.4 Image URLs

- Read models and view models carry the **raw TMDB path** (`/abc.jpg`); the **view** builds the full
  URL and **chooses the size** (a presentation decision), via the `<img asp-tmdb-poster>` tag-helper
  backed by `ITmdbImageUrlBuilder.Build(path, size)` → `{ImageBaseUrl}/{size}{path}`.
- Sizes by context (never `original` in a list): `w185` cast/thumb, `w342` rail/search cards, `w500`
  detail poster, `w1280`/`original` detail backdrop. Base URL and sizes come from `TmdbOptions` — never
  hardcoded (skill rule).

---

## 3. Caching (in-memory `IDistributedCache`, cache-aside) — ADR 0007

- **Free provider only: `AddDistributedMemoryCache()`.** No Redis, no Memurai, no Azure. `IDistributedCache`
  is the seam so a Redis-compatible provider *could* be swapped at registration later — out of scope now.
- **Cache-aside lives in a decorator, not the handlers.** `CachedTmdbClient : ITmdbClient` wraps the raw
  `TmdbClient`. Handlers call `ITmdbClient` and are oblivious to caching (SRP: raw client = HTTP+map;
  decorator = cache-aside + graceful degradation). Cached values are the **Application read models**, so
  a cache hit is byte-for-byte the same shape a live call returns — the decorator is transparent.
- **Key convention** `cinora:tmdb:{resource}:{media}:{args}`:

  | Resource | Key | TTL | Notes |
  |---|---|---|---|
  | Trending | `cinora:tmdb:trending:{media}` | 1 h ±10% | Home rail |
  | Popular | `cinora:tmdb:popular:{media}` | 3 h ±10% | Home rail |
  | Top-rated | `cinora:tmdb:toprated:{media}` | 12 h ±10% | Changes slowly |
  | Search | `cinora:tmdb:search:{media}:{normalizedQuery}:{page}` | 15 min | Normalize (trim+lower) to bound keys; short TTL caps churn |
  | Details | `cinora:tmdb:details:{media}:{tmdbId}` | 24 h ±10% | Metadata changes rarely |
  | Genres | `cinora:tmdb:genres:{media}` | 24 h | Small, bounded |

- **Graceful degradation (mandatory).** Every cache read/write is wrapped in try/catch → `LogWarning` →
  fall through to the source. **A cache outage must never surface as an error page** (skill rule).
- **Never cache failures.** Only successful, non-null payloads are stored; a 404/empty details response
  (`null`) and search misses are not cached (skill rule — otherwise a transient blip poisons the key).
- **Stampede protection.** ±10% TTL jitter on the shared hot keys (rails) so they do not expire in
  unison; a per-key `SemaphoreSlim` guard on the trending/popular keys is optional and noted for the
  performance-agent (single-instance in-memory cache makes this lower-risk than a distributed one).
- **`DefaultTtlSeconds`** from `CacheOptions` is the fallback when a caller passes no TTL; the per-resource
  TTLs above are the intended values.

---

## 4. Catalog persistence — `Movie` / `Genre` / `MovieGenre` (ADR 0007)

The persistence question is *when* the internal `Movie`/`Genre` rows are written and how they coexist
with the cache. The two stores are **complementary, not redundant**:

- **Cache** = display-latency accelerator over TMDB read models. **Source of truth: TMDB.**
- **DB (`Movie`/`Genre`/`MovieGenre`)** = durable **internal-identity anchor**. It supplies the internal
  `Guid` that Phase 3–4 entities (`Review`, `Watchlist`, `Comment`) reference, and the `TmdbGenreId→name`
  map used to label cards (TMDB list/search payloads carry only `genre_ids`).

### 4.1 Decisions

- **Genres: persisted eagerly in Phase 2.** `SyncGenresCommand` idempotently upserts by `TmdbGenreId`
  from `/genre/movie/list` + `/genre/tv/list`. It supersedes/aligns the Phase 1 static `GenreSeedData`.
  Justification: genres are a tiny bounded set and are **read** this phase to resolve `genre_ids` → names
  on rail/search cards. Runnable on demand (diagnostics/admin trigger) and/or once at startup.
- **Movies + `MovieGenre`: read-through, first-touch persistence on the Details view**, implemented as an
  **idempotent `EnsureTitleCachedCommand(MediaType, TmdbId)`** (a *command* — preserving CQRS query
  purity; see §5), dispatched by the Details controller **after** the display query. It existence-checks
  by the unique `(TmdbId, MediaType)` index (one cheap indexed read in steady state) and upserts on miss
  via `Movie.FromTmdb(...)` + `MovieGenre.Link(...)`. Because the display query just warmed the cache,
  the command's `ITmdbClient` read is a cache hit — no extra TMDB round-trip. This is the **one canonical
  seam** Phase 3 (`CreateReviewCommand`) and Phase 4 (`AddToWatchlistCommand`) reuse.
- **Rails and search do NOT persist.** Persisting every trending/search card would bloat the catalog
  with titles no one interacts with. Only a title a user actually opens (details) earns a row.
- **Coexistence, Phase 2 reality (stated honestly):** in Phase 2 **no display read queries the `Movie`
  table** — display comes from cache→TMDB. The `Movie` table is *write-mostly* this phase, populated so
  Phase 3–4 have the internal-`Guid` rows they need. Genres *are* read from the DB (id→name). Local-catalog
  **search merge** (the phase brief's "TMDB + local catalog") is **deferred**: the Phase 2 catalog is too
  sparse for a union to add value; keep Phase 2 search TMDB-only and revisit once the catalog fills
  (Phase 3+). Flagged for the orchestrator.

### 4.2 Background refresh (Hangfire) — DEFERRED out of Phase 2

- **Decision: defer the nightly `TmdbSyncJob`.** Rationale (YAGNI applied to architecture): in Phase 2
  **nothing reads persisted movie metadata**, so "stale metadata" has no user-visible effect yet, and the
  24 h details cache TTL already keeps the *display* path fresh from TMDB. Standing up Hangfire (a new
  dependency, a `HangfireServer`, a dashboard to secure with an `IDashboardAuthorizationFilter`, and
  idempotent jobs) purely to refresh rows no feature reads is premature.
- **When it returns:** the phase where persisted metadata is first *consumed* (Phase 3 renders title
  metadata from the local `Movie` row, or Phase 5 AI needs a populated catalog). At that point **Hangfire
  OSS + SQL Server storage (free, no Docker)** is the right choice: idempotent upsert by `(TmdbId,
  MediaType)`, UTC `Cron.Daily`, `Movie.CachedAtUtc` as the staleness marker (the field already exists),
  admin-gated dashboard.
- **Exit-criterion tension (flagged, not silently dropped):** the phase brief lists "nightly refresh job
  registered and runnable via Hangfire dashboard" as an In-Scope item *and* an Exit Criterion. This design
  recommends deferring it. If the user prefers to honor the criterion as written, **optional Milestone 2.6**
  (§9) adds a minimal, mock-testable Hangfire setup. The recommendation is to descope it from Phase 2 exit.

---

## 5. Feature verticals via `ISender`

All in `Cinora.Application/Features/Discovery/`. Controllers inject `ISender`, return full views or HTMX
partials (aspnet-core-mvc skill). Handlers project TMDB read models → view-model DTOs; they never expose
TMDB DTOs or Domain entities to the view.

| Request | Kind | Handler dependencies | Returns |
|---|---|---|---|
| `GetHomeFeedQuery(MediaType Media = Movie)` | Query | `ITmdbClient`, `IAppDbContext` (genre id→name) | `HomeFeedVm` (trending/popular/top-rated rails of `TitleCardVm`) |
| `GetTitleRailQuery(RailKind, MediaType)` | Query | `ITmdbClient` | `RailVm` — one rail, for per-rail HTMX lazy-load |
| `SearchTitlesQuery(string Query, MediaType Media, int Page = 1)` | Query (+ validator) | `ITmdbClient` | `SearchResultsVm` (page of `TitleCardVm` + paging cursor) |
| `GetTitleDetailsQuery(MediaType Media, int TmdbId)` | Query | `ITmdbClient` | `TitleDetailsVm?` (hero, metadata, cast, rating placeholder, watchlist-stub flag) |
| `EnsureTitleCachedCommand(int TmdbId, MediaType MediaType)` | Command (idempotent) | `IAppDbContext`, `ITmdbClient` | `EnsureTitleCachedResult` (`Outcome` + `Guid? MovieId`) |
| `SyncGenresCommand()` | Command (idempotent) | `IAppDbContext`, `ITmdbClient` | `SyncGenresResult` (`Inserted` + `AlreadyPresent`) |

- **CQRS purity is preserved:** queries never call `SaveChangesAsync`; the only writes are the two
  commands. `EnsureTitleCachedCommand` is dispatched by the **controller** after the read query (not
  handler-to-handler — no `ISender` inside a handler, per the cqrs skill).
- **`SearchTitlesQuery` has a FluentValidation validator** (query required, 1–100 chars, `Page ≥ 1`) — the
  first real use of the Phase 1 `ValidationBehavior`. `GetHomeFeed`/`GetTitleDetails` need no validator.
- **`GetHomeFeedQuery`** parallelizes its three rail calls with `Task.WhenAll`; alternatively the page
  ships skeletons and each rail is an independent HTMX partial (`GetTitleRailQuery`) that the browser
  loads in parallel — the recommended UI approach (§6) since it also isolates skeletons and caching.
- **Controllers (sketch):** a `DiscoveryController` with `[AllowAnonymous]` GET actions
  `Index`(home)/`Rail`/`Search`/`Details`; `Details` also `await sender.Send(new EnsureTitleCachedCommand(...))`.

---

## 6. UI and the three explicit contract extensions

**Screens** (frontend-agent + ux-agent; skills: `premium-ui-design`, `razor-views`,
`alpine-htmx-interactivity`, `ui-animations`, `responsive-accessibility`):

- **Home** — trending / popular / top-rated **rails** of 2:3 poster cards (`w342`), horizontal scroll,
  hover lift. Page ships the shell + **skeleton loaders**; each rail streams in via HTMX
  (`hx-get` + `hx-trigger="load"`) so the three TMDB calls do not block first paint and each rail caches
  independently. `loading="lazy"` + `decoding="async"` on posters.
- **Search** — Alpine `x-data` **debounce (~300 ms)** on the input → HTMX `hx-get` to the search partial;
  results are `TitleCardVm` cards; **load-more pagination** via a sentinel row (`hx-trigger="revealed"`)
  advancing TMDB's `page` param (TMDB is page/offset-based — keyset applies only to *our* DB feeds, Phase 3+).
- **Details** — **backdrop hero** (`w1280`/`original`) with a **gradient scrim** (premium-ui rule: never
  text directly on art), poster (`w500`), metadata (year, runtime, genres, TMDB vote as a labeled
  reference), **cast summary** (top ~10 from credits, `w185` profiles), **community-rating placeholder**
  (our reviews arrive Phase 3), and an **add-to-watchlist STUB** button (visually present, wired in Phase 4).

**Three pre-recorded contract extensions Phase 2 MUST make (REVIEW_BACKLOG Phase-2 gates):**

1. **Public browse actions get explicit `[AllowAnonymous]`.** The global fallback policy is fail-closed;
   Home/Search/Details are public, so each browse action must opt out explicitly (a forgotten attribute =
   accidental auth wall, not a hole — but still wrong). GET-only browse is exempt from anti-forgery.
2. **CSP `img-src` adds `https://image.tmdb.org`.** Extend the `SecurityHeadersMiddleware` CSP from
   `img-src 'self' data:` to `img-src 'self' https://image.tmdb.org data:` (posters, backdrops, and cast
   profiles are all on that host; keep `data:` for skeleton placeholders). Do **not** widen any other
   directive.
3. **Search's Alpine debounce requires the `@alpinejs/csp` build.** Phase 2 is the first phase with
   interactive Alpine. To keep `script-src 'self'` strict (no `'unsafe-eval'`), switch to the
   **`@alpinejs/csp`** distribution (REVIEW_BACKLOG note). Any future browser write (watchlist POST,
   Phase 4) carries the anti-forgery token via the already-wired `RequestVerificationToken` header +
   layout `<meta>` — Phase 2 browse is otherwise all GET.

---

## 7. Performance stance

(performance-agent; skills: `dotnet-performance`, `redis-caching`.)

- **Image size variants** per §2.4 — never `original` in a list; `w342` cards, `w185` cast.
- **Lazy images** (`loading="lazy"`, `decoding="async"`); fixed 2:3 aspect boxes to avoid layout shift.
- **`OutputCache`** on the cacheable anonymous GET partials (home rails, details) with short TTLs aligned
  to the TMDB cache and tags for later invalidation; safe because browse is anonymous (no per-user vary).
- **Async end-to-end**, `CancellationToken` threaded controller→handler→`ITmdbClient`/EF (no `.Result`).
- **`AsNoTracking()` + `Select` projections** for the only local reads this phase (genre id→name, the
  `EnsureTitleCachedCommand` existence check). Keyset pagination is reserved for **local** feeds (Phase 3+);
  TMDB rails/search must use TMDB's `page` param (external-API constraint).
- **Parallelism:** home rails fetched concurrently (`Task.WhenAll`) or as independent HTMX partials.
- The **resilience handler** (§2.2) is itself a performance safeguard: it converts transient TMDB blips
  into bounded retries/circuit-breaks instead of user-facing 500s or hung requests.

---

## 8. Key risks baked into the design

- **Live TMDB key required for live verification only.** Mock-first milestones (2.0–2.5) build and test
  with a **faked `ITmdbClient`** and a **dummy `Tmdb:ApiKey`** (satisfies `ValidateOnStart`). The user's
  free **v4 Read Access Token** is needed only for the end-of-phase live smoke and the live contract test.
  Sequence all mock-first work ahead of that (§9).
- **`ValidateOnStart` on `TmdbOptions` fails boot without a key** — intended fail-closed. The risk is
  breaking existing Phase 1 tests: mitigated by a dummy key in the Testing configuration (§9, 2.0).
- **In-memory cache is per-process** (ADR 0004): no cross-instance sharing; acceptable for a single-instance
  local build, and every value is reconstructable from TMDB. A cache outage degrades to the slow path.
- **Write-on-GET for `EnsureTitleCachedCommand`:** a details GET triggers an idempotent upsert. Mitigated
  by the indexed existence check (steady state = one cheap read) and by keeping it a command (CQRS-clean).
  If DB load ever matters, it can move behind a background enqueue when Hangfire lands (§4.2).
- **Hangfire deferral conflicts with the phase brief's exit criterion** (§4.2) — flagged for the user to
  ratify; optional Milestone 2.6 honors it if desired.
- **Unbounded search cache keys** if queries are not normalized — mitigated by trim+lowercase normalization
  and a 15-min TTL.

---

## 9. Phase 2 milestone build order (delegable)

Ordered milestones for the orchestrator. Each states the owning agent(s), the deliverable, the exact
`Verify` command/acceptance, and whether it runs **mock-first (no real TMDB key)** or needs the **live
free key**. Run the mandatory review gates after the relevant milestones (`/review-architecture`,
`/review-code`, `/review-performance`, `/review-security`, `/review-ui`). All commands run from
`D:\Cinora` unless noted.

> **Key sequencing rule:** 2.0–2.5 are **mock-first** — they build and test against a **faked
> `ITmdbClient`** with a **dummy `Tmdb:ApiKey`** in the Testing config (which satisfies the new
> `ValidateOnStart`); **no live TMDB call is made**. Only **2.7 (live verification)** and the live
> contract test in 2.0 need the user's free **v4 Read Access Token** in user-secrets. Do all mock-first
> milestones first; slot the key in before 2.7.

### 2.0 — TMDB client port + adapter + resilience + options (mock-first; one optional live contract test)
- **Owner:** backend-agent (adapter) with architecture-agent sign-off on the port/read-model boundary.
- **Deliverable:** `ITmdbClient` + read models (`Cinora.Application`); `TmdbClient` + internal DTOs +
  `TmdbMapper` (`Cinora.Infrastructure/Tmdb`); `AddHttpClient<TmdbClient>().AddStandardResilienceHandler()`;
  `TmdbOptions.ValidateOnStart()` switched ON; `ITmdbImageUrlBuilder` + impl; **dummy `Tmdb:ApiKey` added
  to the Testing configuration** so the suite boots. New package: `Microsoft.Extensions.Http.Resilience`
  (pin centrally).
- **Verify (mock-first):**
  ```
  dotnet build Cinora.sln -c Release
  dotnet test tests/Cinora.Application.Tests/Cinora.Application.Tests.csproj
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  Unit tests map **recorded TMDB JSON fixtures** → read models (no network). An integration test asserts
  the app **boots** with the dummy key and **fails fast** when `Tmdb:ApiKey` is blank (`ValidateOnStart`).
  Build clean (TWAE on).
- **Live (optional, needs key):** a `[Trait("Category","Live")]` contract test hitting real TMDB for
  trending/details — run manually once the key is in user-secrets; excluded from the default suite.

### 2.1 — Caching decorator (mock-first)
- **Owner:** backend-agent + performance-agent.
- **Deliverable:** `CachedTmdbClient` decorator; `AddDistributedMemoryCache()`; key convention + per-resource
  TTLs + jitter; graceful degradation; never-cache-null; `ITmdbClient` → decorator registration.
- **Verify (mock-first):**
  ```
  dotnet test tests/Cinora.Application.Tests/Cinora.Application.Tests.csproj
  ```
  With a **fake inner `ITmdbClient`** + a real in-memory `IDistributedCache`: a second identical call is a
  **cache hit** (inner invoked once); a thrown cache backend **degrades** to the inner (no error); a `null`
  details response is **not cached**. (Some teams place these in an Infrastructure test project; either is
  fine — state where.)

### 2.2 — Catalog persistence (genres + first-touch title) (mock-first, LocalDB)
- **Owner:** backend-agent + database-agent.
- **Deliverable:** the two catalog-persistence command handlers (persisting via `IAppDbContext`, no
  dedicated port — ADR 0008): `SyncGenresCommand` (idempotent genre upsert by `TmdbGenreId`);
  `EnsureTitleCachedCommand` (idempotent `Movie`/`MovieGenre` upsert by `(TmdbId, MediaType)`, mapping read
  model → `Movie.FromTmdb`). No new migration expected (entities/index exist since Phase 1) — confirm and
  add one only if a mapping tweak requires it.
- **Verify (mock-first, LocalDB):**
  ```
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  With a **faked `ITmdbClient`**: `SyncGenresCommand` run twice leaves genres upserted (no duplicates);
  `EnsureTitleCachedCommand` run twice for the same `(TmdbId, MediaType)` yields the **same `Movie.Id`** and
  one row; genre links resolve to existing `Genre` rows.

### 2.3 — Home feed vertical (mock-first tests; live imagery needs key)
- **Owner:** backend-agent (query/handler/controller) + frontend-agent + ux-agent.
- **Deliverable:** `GetHomeFeedQuery`/`GetTitleRailQuery` + handlers; `DiscoveryController.Index/Rail`
  (`[AllowAnonymous]`); rail partials + skeleton loaders + poster cards + TMDB image tag-helper; **CSP
  `img-src` += `https://image.tmdb.org`**.
- **Verify (mock-first):**
  ```
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  With a **faked `ITmdbClient`**: anonymous GET `/` (or `/home` browse) returns 200 and renders three
  rails; the response carries the **extended CSP** (`image.tmdb.org` present). Then `/review-ui`,
  `/review-performance` (home page weight). **Live imagery** confirmed manually in 2.7.

### 2.4 — Search vertical (mock-first; CSP Alpine build)
- **Owner:** backend-agent + frontend-agent.
- **Deliverable:** `SearchTitlesQuery` + **validator**; `DiscoveryController.Search` (`[AllowAnonymous]`);
  Alpine debounce input + HTMX partial results + load-more pagination; **switch to the `@alpinejs/csp`
  Alpine build** so `script-src 'self'` stays strict.
- **Verify (mock-first):**
  ```
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  npm run build --prefix src/Cinora.Web
  ```
  Faked client: a query returns paged cards; an empty query returns **400 ValidationProblemDetails** via
  `ValidationBehavior`; the built bundle uses the CSP-safe Alpine build (no `eval`). `/review-security`
  (CSP not weakened), `/review-ui`.

### 2.5 — Details vertical + first-touch persistence wiring (mock-first)
- **Owner:** backend-agent + frontend-agent + ux-agent.
- **Deliverable:** `GetTitleDetailsQuery` + handler; `DiscoveryController.Details` (`[AllowAnonymous]`,
  dispatches `EnsureTitleCachedCommand` after the read query); backdrop hero + scrim, cast summary,
  community-rating placeholder, add-to-watchlist **stub**.
- **Verify (mock-first, LocalDB):**
  ```
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  Faked client: anonymous GET details returns 200 with hero + cast + rating placeholder + stub; the same
  request **persists exactly one** `Movie` row and is idempotent on repeat. `/review-ui`, `/review-code`.

### 2.6 — (OPTIONAL) Nightly Hangfire refresh — only if the user overrides the §4.2 deferral
- **Owner:** backend-agent (+ security-agent for the dashboard filter).
- **Deliverable:** Hangfire OSS + SQL Server storage; `TmdbSyncJob` (idempotent upsert by `(TmdbId,
  MediaType)`, uses `Movie.CachedAtUtc`); `RecurringJob.AddOrUpdate(..., Cron.Daily)`; admin-gated
  `/jobs` dashboard.
- **Verify (mock-first):**
  ```
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  Faked client: the job is registered and **runnable on demand**, and a run upserts without duplicates;
  the dashboard 401/403s anonymously. `/review-security`.

### 2.7 — Live verification (NEEDS the user's free TMDB v4 Read Access Token)
- **Owner:** backend-agent + performance-agent (manual/gated).
- **Deliverable:** the real key in user-secrets (`Tmdb:ApiKey`); a live run of Home/Search/Details.
- **Verify (live):**
  ```
  dotnet user-secrets set "Tmdb:ApiKey" "<user's v4 read access token>" --project src/Cinora.Web
  dotnet run --project src/Cinora.Web/Cinora.Web.csproj
  # then, against the running app (anonymous):
  #   GET /            → real trending/popular/top-rated posters render
  #   GET /search?q=.. → real results, debounced
  #   GET details      → real backdrop + cast
  ```
  Home/Search/Details render **real TMDB-backed data** with skeletons and responsive layout; **repeat
  requests hit the in-memory cache** (verifiable via the `CachedTmdbClient` cache-hit log / absence of a
  second TMDB call in logs), respecting TMDB rate limits.

**End-of-phase gate (adjusted Exit Criteria):** `dotnet build Cinora.sln -c Release` clean;
`dotnet test` green across all three test projects (mock-first, no live key); Home/Search/Details render
real TMDB data live (2.7); repeat requests demonstrably hit the in-memory cache; **zero Critical/High**
findings across the five review gates. The "nightly Hangfire job" exit item is **descoped to optional
2.6** per §4.2 (user to ratify).

---

## 10. Milestone 2.3 implementation spec — Home feed / discovery rails (contract)

- **Status:** Accepted refinement of §5/§6/§9.2.3 (2026-07-02). Design-only; no code written. Resolves the
  one open question (routing/page structure) and pins the query/VM/tag-helper/CSP/test contract so
  backend-agent and frontend-agent can build **2.3 in parallel** without guessing. Where this section
  differs from the §5 sketch, this section governs for 2.3 (it is the accepted refinement).

### 10.1 Routing & page structure — RESOLVED

**Decision: a dedicated `DiscoveryController` at `/discover`.** The public trending/popular/top-rated rails
render at **`GET /discover`** (`[AllowAnonymous]`); the marketing `Landing` at `/` and the authenticated
`Index` shell at `/home` are **unchanged**. *Rationale (one line):* discovery gets a durable,
single-responsibility surface that survives Phase 3 repurposing `/home` into the friends feed, without
disturbing the tested marketing `/` or the authed shell — and Search (2.4) + Details (2.5) nest cleanly
under the same `/discover` space.

| Route | Action | Auth | Purpose | Milestone |
|---|---|---|---|---|
| `GET /discover` | `DiscoveryController.Index` | `[AllowAnonymous]` | Home-feed **shell**: 3 skeleton rails, each lazy-loaded | **2.3** |
| `GET /discover/rail?kind={RailKind}&media={MediaType}` | `DiscoveryController.Rail` | `[AllowAnonymous]` | **one** rail partial (the cards) | **2.3** |
| `GET /discover/search?q=&media=&page=` | `DiscoveryController.Search` | `[AllowAnonymous]` | search (forward reference) | 2.4 |
| `GET /discover/title/{media}/{tmdbId:int}` | `DiscoveryController.Details` | `[AllowAnonymous]` | details (forward reference) | 2.5 |

- `[AllowAnonymous]` sits **at the controller level** (all discovery browse is public; the global fallback
  policy is fail-closed, so the opt-out must be explicit — §6 extension #1). All actions are **GET-only**,
  hence anti-forgery-exempt by construction (the global `AutoValidateAntiforgeryToken` only validates unsafe
  verbs).
- **Anonymous experience:** `/` marketing → primary CTA "Explore Cinora" → `/discover` public rails → card →
  `/discover/title/...` (2.5). **Authenticated experience:** `/home` shell (Phase 2 placeholder → Phase 3
  friends feed); the layout nav gains a **"Discover"** link to `/discover` for everyone. `/discover` being
  `[AllowAnonymous]` means signed-in users browse the same surface.
- **Phase-3 forward-compat:** `/home` becomes the personalized friends feed; `/discover` stays the browse
  surface. Clean separation of *your feed* (authed, `/home`) vs *browse the catalog* (public, `/discover`).
- **2.3 scope:** the shell shows **three Movie rails** (Trending / Popular / Top-Rated). Series rails + a
  media toggle are deferred — a toggle needs interactive Alpine, which pulls in the `@alpinejs/csp` build
  (a **2.4** change per §6 #3). Keeping 2.3 to static HTMX rails means **2.3 does not touch `script-src`**.

### 10.2 Query / handler / VM contract

**Confirm lazy per-rail (the §5-recommended path).** The page ships a shell + skeletons; each rail is an
independent HTMX partial. Consequences:

- **Build exactly ONE query — `GetTitleRailQuery` — in `Cinora.Application/Features/Discovery/`.** It is
  where the real orchestration lives (TMDB fetch + projection) and earns its CQRS keep.
- **Do NOT build `GetHomeFeedQuery` in 2.3.** The §5 table offered it as the *eager* alternative to lazy
  per-rail; since we confirm lazy, an eager query that returns a hardcoded 3-rail set is ceremony around a
  trivial passthrough. The Home **shell is static presentation config built in the controller** (which
  rails, in what order, with what headings). Promote to a real query only if a later phase personalizes the
  rail set (YAGNI).

```
RailKind  (enum, Cinora.Application/Features/Discovery/)
  Trending = 0, Popular = 1, TopRated = 2

GetTitleRailQuery(RailKind Kind, MediaType Media) : IRequest<RailVm>
  Handler deps: ITmdbClient ONLY  (no IAppDbContext, no ISender, no SaveChangesAsync)
  Body: switch Kind → GetTrendingAsync / GetPopularAsync / GetTopRatedAsync (Media, ct)
        project each TmdbTitleSummary → TitleCardVm; return new RailVm(Kind, Media, cards)
  No validator (Kind/Media are enums; the controller guards Enum.IsDefined — see 10.5).
```

**View models** — records; carry the **raw** TMDB poster path; **no Domain entities and no TMDB DTOs**.
(The `MediaType` enum is permitted — it is a shared Domain primitive already used by the Application read
models, not an entity; "no Domain types" means no `Movie`/`Genre`/aggregates.)

```
// Application — Features/Discovery/  (produced by the handler, bound by the partial)
RailVm
  RailKind                    Kind
  MediaType                   Media
  IReadOnlyList<TitleCardVm>  Items

TitleCardVm
  int        TmdbId
  MediaType  MediaType
  string     Title
  int?       ReleaseYear     // = summary.ReleaseDate?.Year (card shows the year, not a DateOnly)
  string?    PosterPath      // RAW TMDB path e.g. "/abc.jpg" — NOT a full URL
  double     VoteAverage     // 0–10 TMDB reference; the card template may render a subtle badge or hide it

// Web — ViewModels/Discovery/  (the static shell; UI copy + hx URLs live in Web, not Application)
HomeFeedViewModel
  IReadOnlyList<RailSlot>  Rails
RailSlot(RailKind Kind, MediaType Media, string Heading)
  // 2.3 default: (Trending, Movie, "Trending This Week"), (Popular, Movie, "Popular"),
  //              (TopRated, Movie, "Top Rated")
```

- **CQRS purity:** the query handler never writes; **rails do NOT persist** (§4.1 — only Details first-touch
  persists). No `ISender` inside the handler; the controller is the only dispatcher.
- `GetTitleRailQuery` responses are already cached by the `CachedTmdbClient` decorator (2.1, per-resource
  TTLs) — the handler stays cache-oblivious.

### 10.3 Genre id→name on cards — RESOLVED

**Decision: no genre chips on Home cards in Phase 2; genres appear on the Details page only.** *Rationale:*
premium 2:3 poster cards read best clean (poster + title + year + optional rating badge); the poster art
already signals tone, so chips add clutter and a hot-path DB dependency for little value. This keeps
`GetTitleRailQuery` **`ITmdbClient`-only** (trivially cacheable, CQRS-pure) and sidesteps the
`genre_ids`→name resolution entirely — Details already receives fully-named genres via
`TmdbTitleDetails.Genres` (no map needed there either). The DB `Genres` map keeps its real Phase-2 job:
`EnsureTitleCachedCommand` link persistence (already built in 2.2).

- **Product/UX flag:** "genre chips on cards" is a product/UX call the user or ux-agent may override. **If
  overridden,** the fallback contract is: give `GetTitleRailQueryHandler` an `IAppDbContext` dependency;
  after the TMDB fetch, load one `AsNoTracking().Select(g => new { g.TmdbGenreId, g.Name })` map filtered to
  the **union of `GenreIds`** on the page; project up to ~2 chip labels per card; add
  `IReadOnlyList<string> Genres` to `TitleCardVm`. Unknown ids (map miss) are skipped, not errors.

### 10.4 TMDB image tag-helper contract

`Cinora.Web/TagHelpers/TmdbImageTagHelper.cs`, constructor-injects `ITmdbImageUrlBuilder`.

```html
<img asp-tmdb-poster="@card.PosterPath" asp-tmdb-size="W342" alt="@card.Title"
     class="h-full w-full object-cover" />
```

- `[HtmlTargetElement("img", Attributes = "asp-tmdb-poster")]`.
  - `asp-tmdb-poster` → `string? Poster` (raw path).
  - `asp-tmdb-size` → `TmdbImageSize Size` (default **`W342`**; §2.4 sizes: `W342` cards, `W185` cast,
    `W500` detail poster, `W1280` backdrop — never `Original` in a list).
- **Process:** `src = builder.Build(Poster, Size)`; when `null` (no path), `src` = same-origin placeholder
  `~/img/poster-placeholder.svg` (covered by `img-src 'self'`). Add `loading="lazy"` and `decoding="async"`
  unless the caller already set them. The tag helper does **not** invent `alt` — the caller supplies it
  (accessibility). It does not set width/height; the **card wraps the img in a fixed `aspect-[2/3]` box**
  (`<div class="aspect-[2/3] overflow-hidden …"><img … class="h-full w-full object-cover"></div>`) so
  posters reserve space and **avoid CLS**.
- **Registration:** add `@addTagHelper *, Cinora.Web` to `src/Cinora.Web/Views/_ViewImports.cshtml` (only
  the framework tag helpers are registered today).

### 10.5 Controller contract (Web)

```
[AllowAnonymous]
[Route("discover")]
public sealed class DiscoveryController(ISender sender) : Controller
{
    [HttpGet("")]                                   // GET /discover
    public IActionResult Index()
        => View(HomeFeedViewModel.Default);         // static 3-Movie-rail shell; NO ISender, NO TMDB, NO DB

    [HttpGet("rail")]                               // GET /discover/rail?kind=Trending&media=Movie
    public async Task<IActionResult> Rail(RailKind kind, MediaType media, CancellationToken ct)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(media)) return BadRequest();   // 400 on bogus enum
        var vm = await sender.Send(new GetTitleRailQuery(kind, media), ct);
        return PartialView("_Rail", vm);            // partial only — no layout
    }
}
```

- The `Enum.IsDefined` guard is required: a plain (non-`[ApiController]`) controller does **not** auto-400
  on an unmatched enum string — it would silently bind the default (`Trending`). The guard makes `?kind=Bogus`
  a real `400`.
- `CancellationToken` threads controller → `ISender` → handler → `ITmdbClient` (bound from
  `HttpContext.RequestAborted`).

**Views** (frontend-agent; skills `razor-views`, `premium-ui-design`, `alpine-htmx-interactivity`,
`ui-animations`, `responsive-accessibility`):

- `Views/Discovery/Index.cshtml` — `@model HomeFeedViewModel`; for each `RailSlot` render the **heading in
  the shell** (visible before cards load) + a body div that lazy-loads the rail:
  `<div hx-get="@Url.Action("Rail", "Discovery", new { kind = slot.Kind, media = slot.Media })"
        hx-trigger="load" hx-swap="innerHTML"> …skeleton cards… </div>`.
- `Views/Discovery/_Rail.cshtml` — `@model RailVm`; horizontal-scroll scroller of `_TitleCard` (heading is
  NOT re-rendered here — it stays in the shell; the partial swaps only the cards). Empty `Items` → a quiet
  empty state, not an error.
- `Views/Discovery/_TitleCard.cshtml` — `@model TitleCardVm`; `aspect-[2/3]` poster box using the tag helper,
  title, `ReleaseYear`, optional TMDB rating badge; wrapped in an anchor to
  `href="/discover/title/@(Model.MediaType.ToString().ToLowerInvariant())/@Model.TmdbId"` (Details lands in
  2.5; the link pattern is fixed here so 2.5 must honor `title/{media}/{tmdbId:int}`).

### 10.6 CSP extension — exact edit

In `src/Cinora.Web/Infrastructure/SecurityHeadersMiddleware.cs`, change **only** the `img-src` directive:

```
- "img-src 'self' data:; " +
+ "img-src 'self' https://image.tmdb.org data:; " +
```

- **Nothing else widened.** `connect-src` stays `'self'`: the HTMX rail GETs are **same-origin**
  (`/discover/rail`) → already allowed. `script-src` stays `'self'`: 2.3 uses only HTMX (attribute-driven
  fetch, **no eval**) → the `@alpinejs/csp` switch is a **2.4** concern (no Alpine `x-data` on the Home page).
- Update the middleware's `FUTURE:` comment to record that the `img-src` extension is now applied (Phase 2).

### 10.7 Mock-first test plan (`tests/Cinora.Web.IntegrationTests/Discovery/`, faked `ITmdbClient`)

**Test-infra prerequisite (testing-agent):** extend `FakeTmdbClient` so the three rail members return
configurable canned lists instead of throwing — add settable `TrendingMovies/PopularMovies/TopRatedMovies`
(and Series equivalents, defaulting `[]`) and have `GetTrendingAsync/GetPopularAsync/GetTopRatedAsync`
return them by `MediaType`. (The 2.2 catalog tests never call rails, so removing the throw is safe.)

| # | Test | Asserts |
|---|---|---|
| T1 | `Index_anonymously_returns_200_with_three_lazy_rail_shells` | `GET /discover` (no auth, no DB/TMDB needed) → **200**; body contains **three** `hx-get` URLs to `/discover/rail` for `kind=Trending/Popular/TopRated` (`media=Movie`) + skeleton markers. |
| T2 | `Rail_partial_anonymously_returns_200_with_cards` | Fake seeded (Trending Movie → 2–3 summaries incl. one with `PosterPath = null`); `GET /discover/rail?kind=Trending&media=Movie` → **200**, partial (no layout); N card anchors, poster `src` = `https://image.tmdb.org/.../w342/...`, the null-poster card renders the **placeholder** (no crash), titles + Details hrefs present. |
| T3 | `Discovery_responses_carry_the_extended_CSP` | `GET /discover` **and** the rail GET → CSP header contains `img-src 'self' https://image.tmdb.org data:`; `script-src 'self'` **unchanged** and **no `unsafe-eval`** (extend the existing `SecurityHeadersTests` or a sibling). |
| T4 | `Rail_GET_writes_nothing_to_the_database` | After a rail GET, `Movies` row count is unchanged — proves query purity (rails never persist, §4.1). |
| T5 | `Rail_with_a_bogus_kind_returns_400` | `GET /discover/rail?kind=Bogus&media=Movie` → **400** (the `Enum.IsDefined` guard). |

- Confirms the design invariants: `[AllowAnonymous]` + **GET-only** (T1/T2 succeed with no anti-forgery
  token → GET is exempt), **CQRS purity** (T4; handler has no `IAppDbContext`/`SaveChangesAsync`, no
  `ISender`), and the **extended CSP** (T3). Live imagery is confirmed manually in 2.7.
- Run after 2.3: `/review-ui`, `/review-performance` (home page weight), `/review-security` (CSP not
  otherwise weakened).

### 10.8 Deltas from the §5 sketch (for reviewers)

1. Home route pinned to **`/discover`** (new `DiscoveryController`), not `/` or `/home`.
2. **`GetHomeFeedQuery` is not built in 2.3** (lazy per-rail confirmed → the shell is static controller
   config); one query, `GetTitleRailQuery`, remains.
3. **Genre chips deferred to Details** → `GetTitleRailQuery` is `ITmdbClient`-only (drops the §5 table's
   `IAppDbContext` dependency for Home). Product/UX-overridable (10.3 fallback).
4. `HomeFeedViewModel` lives in **Web** (shell/UI copy), while `RailVm`/`TitleCardVm` live in **Application**
   (handler output).

---

## 11. Milestone 2.4 implementation spec — Search (contract)

- **Status:** Accepted refinement of §5 (`SearchTitlesQuery` vertical), §6 (Search UI + contract extension
  #3, the `@alpinejs/csp` build) and §9.2.4 (2026-07-03). Design-only; no code written. Pins the query / VM /
  validator / controller / Alpine-component / load-more / failure / test contract so backend-agent and
  frontend-agent can build **2.4 in parallel** without guessing. Where this section refines the §5/§6
  sketch, this section governs for 2.4 (deltas listed in §11.12).

### 11.1 Scope and the one big risk

**In scope:** a search page + an HTMX results partial under `/discover`, a `SearchTitlesQuery` vertical
(`ITmdbClient`-only, CQRS-pure) with the phase's **first real FluentValidation validator**, sentinel-row
load-more pagination, the 2.3 failure-state grammar reused, and the switch from standard Alpine to the
**`@alpinejs/csp`** build.

**BIGGEST RISK (call it out plainly): the `@alpinejs/csp` build swap.** Standard Alpine evaluates every
`x-data` / `x-on` / `x-bind` expression with `new Function(...)` (an `eval` family call), which the strict
`script-src 'self'` CSP blocks — so the moment Search ships interactive Alpine under the existing CSP, the
component silently does nothing. The fix is to swap the npm dependency to the CSP distribution (which uses a
restricted, `eval`-free evaluator) **and** migrate the markup to the CSP build's stricter rules (registered
components + name-only references, no inline expressions). Two things must both be true for 2.4 to be done:
the bundle **restores offline** (`@alpinejs/csp` installs and esbuild bundles it) and the **markup obeys the
name-only rule**. Verification pre-done for the implementer: `@alpinejs/csp@3.15.12` is published, is the
**exact version match** for the installed `alpinejs@3.15.12`, and ships `dist/module.esm.js` (esbuild's
entry) plus its own `src/evaluator.js`/`src/parser.js` (the restricted evaluator). The implementer must
still run `npm install` + `npm run build` and confirm the bundle contains **no `eval`/`new Function` from
Alpine** (§11.11 B1). Everything else in 2.4 is low-risk plumbing over the proven 2.3 pattern.

**Net security posture (confirm):** the `@alpinejs/csp` evaluator needs **no `'unsafe-eval'`**, so
`SecurityHeadersMiddleware` is **unchanged** in 2.4 — the ONLY security-relevant change is the npm build
swap. `img-src` was already extended for TMDB in 2.3; nothing else widens (§11.9).

### 11.2 The `@alpinejs/csp` switch — exact deltas (highest-risk item)

**(a) `package.json` — swap the dependency (keep the version pinned to the installed Alpine).**

```
  "dependencies": {
-   "alpinejs": "^3.15.12",
+   "@alpinejs/csp": "^3.15.12",
    "htmx.org": "^2.0.10"
  }
```

- Then `npm install` (writes the lockfile) and remove the now-unused `alpinejs` entry. **Version caveat
  (must verify):** the CSP build is versioned in lockstep with core Alpine; pin the **same** `3.15.x` you had
  for `alpinejs` (verified available: `3.15.12`). If a future `npm install` cannot resolve a matching CSP
  version, do **not** fall back to standard Alpine under the strict CSP (that reintroduces `eval` and breaks
  silently) — hold the version or, only as a last resort, add a CSP-hashed/nonce path (a separate ADR).

**(b) `Scripts/site.ts` — import the CSP build, register components BEFORE start.**

```ts
// BEFORE (bundles standard Alpine — eval-based, blocked by script-src 'self')
import Alpine from "alpinejs";
...
window.Alpine = Alpine;
...
Alpine.start();

// AFTER (CSP build — restricted evaluator, no eval; components registered by name before start)
import Alpine from "@alpinejs/csp";
import { registerSearch } from "./components/search";
...
window.Alpine = Alpine;        // keep for debugging (harmless)
window.htmx = htmx;
document.addEventListener("htmx:configRequest", attachAntiforgeryHeader);

registerSearch(Alpine);        // MUST run before start(): x-data="search" resolves a registered component
Alpine.start();
```

- Delete/replace the misleading line-41 comment ("…inline `x-data` / `hx-*` expressions"): under the CSP
  build **inline `x-data`/`x-on` expressions are forbidden** — components are registered via `Alpine.data`
  and referenced by name. The `hx-*` attributes are unaffected (HTMX is attribute-driven, not `eval`).

**(c) `Scripts/types/alpinejs.d.ts` — re-point the ambient module to `@alpinejs/csp`.** The CSP package
ships **no types** (verified: no `types`/`exports` field), so the hand-rolled ambient declaration is still
required. Change the module name and export the interface so components can type their `Alpine` parameter:

```ts
declare module "@alpinejs/csp" {          // was: "alpinejs"
  export interface Alpine {                // export so components can import the type
    start(): void;
    data(name: string, callback: (...args: unknown[]) => object): void;  // return `object` keeps strict tsc happy
    store(name: string, value?: unknown): unknown;
    // …directive/magic/plugin unchanged…
  }
  const Alpine: Alpine;
  export default Alpine;
}
```
(Filename may stay `alpinejs.d.ts` — only the module name inside is load-bearing. The `data(...) => object`
return, rather than `Record<string, unknown>`, lets a component return a typed interface without an index
signature under `strict` + `exactOptionalPropertyTypes`.)

**(d) The CSP-build MARKUP RULE (the migration every Razor `x-*` attribute must obey).** In the CSP build a
directive value may only be a **bare reference to a registered data property or method by name** — never an
inline expression, operator, literal, or call with arguments:

| Allowed (name-only) | Forbidden in CSP build (inline expression) |
|---|---|
| `x-data="search"` | `x-data="{ q: '' }"` |
| `x-model="query"` | `x-model="form.query"` is fine (dotted path ok); `x-model="q || ''"` is not |
| `x-on:input.debounce.300ms="onInput"` | `x-on:input="q = $event.target.value"` |
| `x-show="loading"` | `x-show="!loading"` / `x-show="items.length"` |
| `x-bind:aria-busy="loading"` | `:aria-busy="loading ? 'true':'false'"` |

- **Consequence for the component design:** anything needing logic/negation/computation must be a **named
  property or method** on the registered component (e.g. expose `loading`, and if you ever need "not
  loading" expose a second boolean) — you cannot inline it. Modifiers (`.debounce.300ms`, `.prevent`,
  `.stop`) **do** work in the CSP build; use `x-on:input.debounce.300ms="onInput"` for the 300 ms debounce
  (no hand-rolled timer needed).
- **Does NOT reintroduce `hx-on`.** All server round-trips stay attribute-driven HTMX (`hx-get` /
  `hx-trigger` / `hx-target` / `hx-swap`) exactly as in 2.3 — **no `hx-on`, no inline JS** anywhere.

### 11.3 Endpoints & routes (pin)

Two GET actions on the existing `DiscoveryController` (`[AllowAnonymous]` at the controller level, GET-only ⇒
anti-forgery-exempt by construction — §6 #1). They nest under the `/discover/search` space reserved in §10.1.

| Route | Action | Returns | Used by |
|---|---|---|---|
| `GET /discover/search?q=&media=&page=` | `Search` | **Full page** (layout): the search input + a results region; if `q` is valid it **server-renders page 1** into the region (deep-link + no-JS baseline) | Direct navigation, `<form>` submit (no-JS), shareable URL |
| `GET /discover/search/results?q=&media=&page=` | `SearchResults` | **Partial only** (`_SearchResults` / `_SearchPrompt` / `_SearchError`, no layout) | Alpine-fired live search + the load-more sentinel (HTMX) |

- `media` defaults to `Movie` (Series + a media toggle are **deferred** — a product/UX call, §11.12).
- `page` defaults to `1`; only the sentinel supplies `page > 1`.
- **Progressive enhancement:** the input lives in a `<form method="get" action="/discover/search">`, so
  without JS, Enter reloads `/discover/search?q=…` and the page server-renders results. With JS, Alpine
  intercepts input (debounced) and swaps the `results` partial in place. (No-JS *pagination* — a plain
  `?page=2` "Next" link — is optional polish, §11.12.)

### 11.4 Query / handler / VM / validator contract (Application, `Features/Discovery/`)

```
SearchTitlesQuery(string Query, MediaType Media, int Page = 1) : IRequest<SearchResultsVm>
  Handler deps: ITmdbClient ONLY  (no IAppDbContext, no ISender, no SaveChangesAsync — CQRS-pure)
  Body: var page = await tmdb.SearchAsync(request.Media, request.Query, request.Page, ct);
        map each TmdbTitleSummary → TitleCardVm (REUSE the 2.3 card + mapping — see note);
        return new SearchResultsVm { Items, Query, Media, Page = page.Page,
                                     TotalPages = page.TotalPages, TotalResults = page.TotalResults,
                                     HasMore = page.Page < Math.Min(page.TotalPages, 500) };

SearchResultsVm  (record; Application/Features/Discovery/)
  IReadOnlyList<TitleCardVm>  Items
  string                      Query          // echoed — builds the sentinel URL + the "N results for X" line
  MediaType                   Media          // echoed — builds the sentinel URL
  int                         Page
  int                         TotalPages
  int                         TotalResults
  bool                        HasMore        // Page < min(TotalPages, 500) — TMDB rejects page > 500
```

- **Reuse `TitleCardVm` verbatim** (already in `GetTitleRailQuery.cs`). The `TmdbTitleSummary → TitleCardVm`
  projection is currently the private `GetTitleRailQueryHandler.ToCard`; **DRY refactor (recommended):** lift
  it to a shared static factory `TitleCardVm.FromSummary(TmdbTitleSummary)` colocated with the record, and
  have **both** the rail handler and the search handler call it. One mapping, two callers — avoids the same
  projection drifting in two places.
- **`SearchTitlesQueryValidator : AbstractValidator<SearchTitlesQuery>`** — the phase's **first real
  validator**, auto-discovered by the existing `AddValidatorsFromAssembly(..., includeInternalTypes: true)`
  and enforced by the wired `ValidationBehavior` (which throws the Application `ValidationException`):
  ```
  RuleFor(x => x.Query).NotEmpty().WithMessage("Enter something to search for.")
                       .MaximumLength(100).WithMessage("Search is limited to 100 characters.");
  RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
  ```
  (Add `.Must(q => !string.IsNullOrWhiteSpace(q))` only if you want whitespace-only rejected at the contract
  level; in practice the action/Alpine trim before dispatch, so it never arrives.)
- **Caching is transparent:** `SearchAsync` is already wrapped by the 2.1 `CachedTmdbClient` decorator
  (15-min TTL, `cinora:tmdb:search:{media}:{normalizedQuery}:{page}`, trim+lower normalization). The handler
  stays cache-oblivious; **it does not persist** (only Details first-touch writes a `Movie` row — §4.1).

### 11.5 The empty/too-short query — how the two paths differ (pin; both test-satisfying AND robust)

Three layers, each a **different contract**, so this is defense-in-depth, not ceremony:

1. **Validator (query contract).** Any dispatch of `SearchTitlesQuery("", …)` fails `NotEmpty` ⇒
   `ValidationBehavior` throws `ValidationException` ⇒ the `GlobalExceptionHandler` maps it to **400
   `ValidationProblemDetails`**. This is the guarantee the §9.2.4 test asserts **on a direct `ISender`
   dispatch** (§11.11 S2) — it proves the first validator is really wired into the pipeline.
2. **`SearchResults` action guard (presentation).** For a search-as-you-type box, "empty/too-short input" is
   the **idle state, not an error**. The action therefore guards **before dispatching**: if
   `q?.Trim()` is null or shorter than `MinQueryLength (=2)`, it returns a **200 `_SearchPrompt`** partial and
   **never sends the query**. So no browser path ever surfaces a raw 400 for an empty box, and — critically —
   **HTMX never receives a non-2xx to spin on** (the 2.3 lesson: HTMX won't swap a non-2xx). The validator's
   400 is thus reachable only by a *direct* dispatch (the test), by design.
3. **Alpine min-length guard (client).** `onInput` guards `query.trim().length < minLength (=2)` and returns
   **without firing** any request — so short input costs zero round-trips.

- `MinQueryLength = 2` is a **UX threshold** shared by the action (`const`) and the component (`minLength`);
  keep them in sync. `2` is a product/UX default (TMDB accepts 1 char but returns noise) — overridable
  (§11.12). Note the validator's floor stays **1** (`NotEmpty`); the `2` is a presentation threshold layered
  on top, not a contract change.

### 11.6 Controller contract (Web — add to the existing `DiscoveryController`)

```csharp
private const int MinQueryLength = 2;   // UX threshold; keep in sync with the Alpine component's minLength

[HttpGet("search")]                                   // GET /discover/search  → full page (layout)
public async Task<IActionResult> Search(string? q, MediaType media = MediaType.Movie, int page = 1,
                                        CancellationToken ct = default)
{
    if (!Enum.IsDefined(media)) return BadRequest();   // same guard grammar as Rail (§10.5)
    var term = q?.Trim();
    SearchResultsVm? results = null;
    if (term is { Length: >= MinQueryLength })
        results = await sender.Send(new SearchTitlesQuery(term, media, Math.Max(1, page)), ct);
    return View(new SearchPageViewModel(term ?? string.Empty, media, results));   // _SearchResults shown when results != null, else _SearchPrompt
}

[HttpGet("search/results")]                            // GET /discover/search/results → partial only
public async Task<IActionResult> SearchResults(string? q, MediaType media = MediaType.Movie, int page = 1,
                                               CancellationToken ct = default)
{
    if (!Enum.IsDefined(media)) return BadRequest();
    var term = q?.Trim();
    if (term is not { Length: >= MinQueryLength })
        return PartialView("_SearchPrompt", new SearchPromptViewModel(media));     // 200 idle/prompt — never a 400 to HTMX

    try
    {
        var vm = await sender.Send(new SearchTitlesQuery(term, media, Math.Max(1, page)), ct);
        return PartialView(page > 1 ? "_SearchResultsPage" : "_SearchResults", vm); // page 1 = full region; page>1 = append fragment
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // 2.3 grammar: a post-resilience TMDB failure must NOT become a 500 (HTMX won't swap it). Log Warning,
        // return the on-brand _SearchError retry partial with HTTP 200 so HTMX swaps it and the user retries.
        LogSearchFailed(logger, ex, media, page);
        return PartialView("_SearchError", new SearchErrorViewModel(term, media, page));
    }
}

[LoggerMessage(EventId = 2400, Level = LogLevel.Warning,
    Message = "Discovery search failed for media {Media} page {Page}; serving the error/retry partial (HTTP 200).")]
private static partial void LogSearchFailed(ILogger logger, Exception exception, MediaType media, int page);
```

- **New Web view models** (`ViewModels/Discovery/`): `SearchPageViewModel(string Query, MediaType Media,
  SearchResultsVm? Results)`, `SearchPromptViewModel(MediaType Media)`, `SearchErrorViewModel(string Query,
  MediaType Media, int Page)` — thin presentation records, same pattern as `HomeFeedViewModel` /
  `RailErrorViewModel`. `SearchResultsVm` (the handler output) stays in Application.
- The `Enum.IsDefined(media)` guard mirrors `Rail`'s (a non-`[ApiController]` controller does not auto-400 a
  bad enum). `page`/`q` do not need a 400 guard: `page` is clamped to `≥1` and `q` is guarded to the prompt.

### 11.7 The Alpine `search` component (exact contract) — `Scripts/components/search.ts`

State: `query` (bound to the input via `x-model`), `loading` (drives the spinner + `aria-busy`). Debounce is
the **markup modifier** `.debounce.300ms`. The component **fires HTMX programmatically** via `window.htmx.ajax`
— which is ordinary JS *inside a registered component* (the CSP restriction is on *markup* expressions only,
not on component bodies), so this is fully CSP-safe and needs no `hx-on`.

```ts
// Scripts/components/search.ts
import type { Alpine } from "@alpinejs/csp";

interface SearchData {
  query: string;
  loading: boolean;
  onInput(this: SearchData): void;
}

const MIN_LENGTH = 2;                     // keep in sync with DiscoveryController.MinQueryLength

export function registerSearch(alpine: Alpine): void {
  alpine.data("search", (): SearchData => ({
    query: "",
    loading: false,

    onInput(this: SearchData) {
      const results = document.getElementById("search-results");
      if (!results) return;
      const term = this.query.trim();

      // Min-length guard: below the threshold we do NOT fire — no request, no non-2xx, no spin (2.3 lesson).
      if (term.length < MIN_LENGTH) { this.loading = false; return; }

      const media = results.getAttribute("data-media") ?? "Movie";
      const url = `/discover/search/results?q=${encodeURIComponent(term)}`
                + `&media=${encodeURIComponent(media)}&page=1`;
      this.loading = true;
      void window.htmx
        .ajax("GET", url, { target: "#search-results", swap: "innerHTML" })
        .finally(() => { this.loading = false; });
    },
  }));
}
```

**Markup (CSP-safe, name-only references)** — the search input region inside `Search.cshtml`:

```html
<form method="get" action="/discover/search" x-data="search" role="search" class="…">
  <label for="search-q" class="sr-only">Search movies</label>
  <input id="search-q" name="q" type="search" autocomplete="off" placeholder="Search movies…"
         x-model="query" x-on:input.debounce.300ms="onInput" />
  <!-- spinner: bare property reference only -->
  <span x-show="loading" role="status" aria-hidden="false" class="…">
    <span class="sr-only">Searching…</span> …spinner svg…
  </span>
</form>

<!-- the live region HTMX swaps into; persists across innerHTML swaps so the Alpine binding survives -->
<div id="search-results" data-media="Movie" role="region" aria-label="Search results"
     aria-live="polite" x-bind:aria-busy="loading">
  @if (Model.Results is not null) { <partial name="_SearchResults" model="Model.Results" /> }
  else { <partial name="_SearchPrompt" model="new SearchPromptViewModel(Model.Media)" /> }
</div>
```

- Only bare names appear in `x-*` values (`query`, `onInput`, `loading`) — obeys the §11.2(d) rule. The
  `<form method="get">` is the no-JS fallback; with JS the debounced `onInput` drives the partial swap (Enter
  still does a full-page search, which is fine).
- **Optional polish (flag):** on clearing the box you may reload the prompt by firing the results endpoint
  with the (short) term — it returns a 200 `_SearchPrompt`, no spin. Left out of the core contract to keep it
  tight; stale results simply linger until the next valid keystroke.

### 11.8 Load-more pagination — sentinel row (CSP-safe, attribute-only HTMX)

TMDB is **page/offset-based** (keyset is only for our *local* feeds, §7), so load-more advances the `page`
param toward `TotalPages` (capped at TMDB's max of 500). **Mechanism: a self-replacing sentinel** (the
standard, self-arming HTMX infinite-scroll idiom — chosen over a static `beforeend` sentinel because a static
one does not re-arm after the first `revealed`; §11.12):

- **`_SearchResults`** (page 1, `innerHTML`-swapped into `#search-results`): a results summary
  ("N results for @Model.Query"), then `<ul role="list">` of `<li>` cards (reuse `_TitleCard`), then — **iff
  `HasMore`** — the sentinel as the last `<li>`:
  ```html
  <li hx-get="@Url.Action("SearchResults","Discovery", new { q = Model.Query, media = Model.Media, page = Model.Page + 1 })"
      hx-trigger="revealed" hx-swap="outerHTML" aria-hidden="true">
      …one skeleton card (reserves space)…
  </li>
  ```
- **`_SearchResultsPage`** (page > 1, returned by the sentinel's GET, replaces the sentinel via `outerHTML`):
  the next page's `<li>` cards **plus** — iff still `HasMore` — a fresh sentinel `<li>`. Because `outerHTML`
  replaces the old sentinel in place, the new cards land at the end of the `<ul>` and the next sentinel
  follows. **Terminal state:** when `HasMore` is false the fragment renders **cards with no sentinel**, so the
  chain stops; append a quiet end marker (`<li aria-hidden="true">You've reached the end.</li>`), no re-arm.
- All HTMX here is attribute-driven (`hx-get`/`hx-trigger="revealed"`/`hx-swap="outerHTML"`) — **no `hx-on`,
  no inline JS** → the strict `script-src 'self'` is untouched.

### 11.9 Failure state, accessibility, and CSP posture

- **Failure (reuse the 2.3 `_RailError` grammar).** A post-resilience TMDB error on the results action
  returns a **200 `_SearchError`** (not a 500 — HTMX won't swap a non-2xx). Retry is **attribute-only HTMX**:
  `hx-get` the same `results` URL echoing `q`/`media`/`page`; for a page-1 error target `#search-results`
  with `hx-swap="innerHTML"`; for a load-more (page > 1) error, render the retry inline with
  `hx-swap="outerHTML"` so it re-attempts just that page. `aria-busy` cleared on the error block
  (`aria-busy="false"`), `role="alert"`. No `hx-on`, no inline JS.
- **A11y of HTMX-swapped content.** `#search-results` carries `role="region"`, `aria-label="Search results"`,
  `aria-live="polite"` and `x-bind:aria-busy="loading"`, so both the innerHTML swap (new results) and the
  `beforeend`/`outerHTML` appends (load-more) are announced. The spinner is `role="status"` with a
  visually-hidden "Searching…"; cards remain single-anchor links with the 2.3 accessible-name grammar
  (`_TitleCard`). Reduced-motion: the spinner/skeleton reuse the reduced-motion-safe classes from 2.3.
- **CSP / security (confirm — no change in 2.4).** `SecurityHeadersMiddleware` is **untouched**:
  `script-src 'self'` stays strict (the CSP Alpine evaluator needs **no `'unsafe-eval'`**), `connect-src
  'self'` already allows the same-origin `/discover/search/results` GETs, `img-src` already allows
  `image.tmdb.org` (2.3). The **only** security-relevant change in 2.4 is the npm build swap. Update the
  middleware's `FUTURE:` comment (line ~23) to record that the CSP Alpine build is now applied (Search, 2.4)
  and that `script-src` remains `'self'` with no eval.

### 11.10 Views & files inventory (frontend + backend)

| File | Layer | Purpose |
|---|---|---|
| `Features/Discovery/SearchTitlesQuery.cs` | Application | Query + `SearchResultsVm` + handler (`ITmdbClient`-only) |
| `Features/Discovery/SearchTitlesQueryValidator.cs` | Application | First real validator (Query 1–100, Page ≥ 1) |
| `Features/Discovery/GetTitleRailQuery.cs` (edit) | Application | Add `TitleCardVm.FromSummary(...)`; rail handler reuses it (DRY) |
| `ViewModels/Discovery/SearchPageViewModel.cs` / `SearchPromptViewModel.cs` / `SearchErrorViewModel.cs` | Web | Presentation records |
| `Controllers/DiscoveryController.cs` (edit) | Web | Add `Search` + `SearchResults` actions + `LogSearchFailed` |
| `Views/Discovery/Search.cshtml` | Web | Full page: `x-data="search"` input + `#search-results` region |
| `Views/Discovery/_SearchResults.cshtml` | Web | Page-1 region: summary + `<ul>` cards + sentinel |
| `Views/Discovery/_SearchResultsPage.cshtml` | Web | Load-more fragment: next cards + fresh sentinel / terminal |
| `Views/Discovery/_SearchPrompt.cshtml` | Web | 200 idle/empty state ("Search for a movie…") |
| `Views/Discovery/_SearchError.cshtml` | Web | 200 failure/retry (2.3 `_RailError` grammar) |
| `Scripts/components/search.ts` | Web | The `search` Alpine component (`Alpine.data`) |
| `Scripts/site.ts` (edit) | Web | Import `@alpinejs/csp`; `registerSearch(Alpine)` before `start()` |
| `Scripts/types/alpinejs.d.ts` (edit) | Web | Ambient module → `@alpinejs/csp` |
| `package.json` (edit) | Web | `alpinejs` → `@alpinejs/csp` |
| A nav/Discover "Search" affordance → `/discover/search` | Web | Entry point (small) |

### 11.11 Mock-first test plan

**Test-infra prerequisite (testing-agent): extend `FakeTmdbClient.SearchAsync`** (both copies — the
integration one at `tests/Cinora.Web.IntegrationTests/Infrastructure/FakeTmdbClient.cs` currently *throws*).
Add settable `SearchPages` (`Dictionary<int, IReadOnlyList<TmdbTitleSummary>>` keyed by page, default empty),
`SearchTotalPages`, `SearchTotalResults`, and `bool ThrowOnSearch`; implement `SearchAsync` to return
`new TmdbPage<TmdbTitleSummary> { Page = page, TotalPages = SearchTotalPages, TotalResults = SearchTotalResults,
Items = SearchPages.GetValueOrDefault(page, []) }`, or throw `HttpRequestException` when `ThrowOnSearch`. (No
2.2/2.3 test calls search, so replacing the throw is safe.)

**Integration tests** (`tests/Cinora.Web.IntegrationTests/Discovery/SearchTests.cs`, faked `ITmdbClient`):

| # | Test | Asserts |
|---|---|---|
| S1 | `Search_page_anonymously_returns_200_with_the_input_and_prompt` | `GET /discover/search` (no auth) → **200**; body has `x-data="search"`, the `#search-results` region (`aria-live="polite"`), and the `_SearchPrompt` (no query yet). |
| S2 | `Blank_query_dispatch_yields_400_ValidationProblemDetails` | Resolve `ISender` from the host; `await sender.Send(new SearchTitlesQuery("", Movie, 1))` throws `ValidationException` with a `Query` key (**the validator is wired into `ValidationBehavior`**). Optionally assert `GlobalExceptionHandler` maps it to a 400 `ValidationProblemDetails`. This is the §9.2.4 "direct call" 400. |
| S3 | `Results_partial_returns_200_cards_for_a_valid_query` | Fake seeded (page 1 → 2–3 summaries incl. one `PosterPath=null`); `GET /discover/search/results?q=dune&media=Movie` → **200**, partial (no `<html>`); N card anchors, `w342` `image.tmdb.org` src, null-poster → placeholder, Details hrefs `/discover/title/movie/{id}`, and the "results" summary text. |
| S4 | `Too_short_query_returns_200_prompt_not_400` | `GET /discover/search/results?q=a&media=Movie` → **200** `_SearchPrompt` (guarded before dispatch); **no** 400, so HTMX would swap it (no spin). |
| S5 | `Load_more_advances_the_page_and_appends` | Fake: page 1 + page 2 seeded, `SearchTotalPages=2`. `GET …&page=1` renders a sentinel `hx-get` to `page=2`; `GET …&page=2` returns `_SearchResultsPage` with page-2 cards and **no** further sentinel (terminal). |
| S6 | `Failing_search_returns_200_error_retry_partial` | Fake `ThrowOnSearch=true`; `GET /discover/search/results?q=dune&media=Movie` → **200** (not 500), `role="alert"`, `aria-busy="false"`, a `hx-get` Retry echoing `q`/`media`, and **no** `hx-on`. |
| S7 | `Search_responses_keep_the_strict_CSP` | `GET /discover/search` → CSP header has `script-src 'self'`, **no `unsafe-eval`**, `img-src 'self' https://image.tmdb.org data:` unchanged. |

**Build check (B1) — the highest-risk verification.** After the swap:
```
npm install --prefix src/Cinora.Web
npm run build --prefix src/Cinora.Web        # typecheck (tsc) + esbuild bundle must both pass
```
Then assert the built bundle in `wwwroot/dist/` contains **no `eval(`/`new Function(` originating from
Alpine** (grep the emitted `site-*.js`; the CSP build must not carry the eval-based evaluator). A clean build
+ an eval-free bundle is the proof the CSP switch restored offline.

**Review gates after 2.4:** `/review-security` (CSP not weakened; the eval-free bundle), `/review-ui`
(search interaction, empty/loading/error states, a11y of swapped content), `/review-code`.

### 11.12 Deltas from the §5/§6 sketch, and product-vs-architecture flags

**Architecture refinements (this section governs for 2.4):**
1. **Two actions, not one:** a full **page** (`Search`, no-JS + deep-link baseline) *and* an HTMX **results
   partial** (`SearchResults`). §6 implied a single partial; the page action makes search work without JS.
2. **Empty-query handling pinned to three layers** (§11.5): validator 400 on direct dispatch (tested), action
   guard → 200 prompt (browser path), Alpine guard → no fire (client). Both test-satisfying and robust.
3. **Load-more = self-replacing `outerHTML` sentinel**, not a static `beforeend` sentinel (§11.8). Reason: a
   static `beforeend` sentinel does not re-arm after its first `revealed`; the self-replacing sentinel
   self-arms and gives a clean terminal state. (Realizes the §6 "append + advance page" intent.)
4. **DRY:** `TmdbTitleSummary → TitleCardVm` lifted to `TitleCardVm.FromSummary`, shared by the rail and
   search handlers (§11.4).
5. **`site.ts`/`package.json`/ambient-types deltas pinned** to `@alpinejs/csp@3.15.12` (version-matched,
   ESM + eval-free evaluator verified present).

**Genuine PRODUCT / UX decisions (defer to the user or ux-agent — NOT architecture):**
- **`MinQueryLength = 2`** — the search-as-you-type threshold. `2` is a sensible default; the product owner may
  prefer 1 or 3. (The validator floor stays 1; this is a presentation threshold on top.)
- **Movie-only search + no media toggle in 2.4.** The `media` param is wired for forward-compat, but a
  Movie/Series toggle (now that CSP Alpine exists, it can also be added to the Home rails deferred in §10.1)
  is a scoping/product call. Recommend a follow-up milestone.
- **No-JS "Next page" link** (plain `?page=2` anchor) as a pagination fallback — optional polish.
- **Restore-prompt-on-clear** micro-interaction (§11.7) — optional UX nicety.

_Section 11 authored 2026-07-03 against the shipped 2.3 code (verified: `DiscoveryController`,
`GetTitleRailQuery`/`TitleCardVm`, `_TitleCard`/`_RailError`, `TmdbImageTagHelper`, `ITmdbClient.SearchAsync`
+ `TmdbPage<T>`, `ValidationBehavior` + `GlobalExceptionHandler` → 400 `ValidationProblemDetails`,
`AddValidatorsFromAssembly`, `SecurityHeadersMiddleware` strict CSP, `site.ts`/`build.mjs`/`package.json`,
`FakeTmdbClient`). `@alpinejs/csp@3.15.12` availability/packaging verified via the npm registry. No
application code written._

---

_Design authored 2026-07-02 against the Phase 1 code (verified: `Movie`/`Genre`/`MovieGenre` entities +
`(TmdbId, MediaType)` unique index, `IAppDbContext`, `ISender` pipeline, `TmdbOptions`/`CacheOptions`,
`AddInfrastructure`, `SecurityHeadersMiddleware` CSP, fail-closed authZ + anti-forgery). No code written._
