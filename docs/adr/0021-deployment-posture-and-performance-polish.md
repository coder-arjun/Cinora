# ADR 0021 — Free/Self-Hosted Deployment Posture (Plans-Only) and the Phase-6 Performance-Polish Stance

- **Status:** Accepted
- **Date:** 2026-07-03
- **Phase:** 6 (Polish & Deployment), Milestones 6.4 (performance) + 6.6 (deployment readiness)
- **Deciders:** architecture-agent (design), backend-agent (scripts/config/`/health` — **devops-agent NOT
  used**), performance-agent + security-agent (compression/output-cache ordering), documentation-agent
  (runbook), orchestrator (ratify)

## Context

Phase 6 is the **ship gate**. Two things must be settled without violating the project's constraints:

1. **A deployment posture.** The phase brief names **Azure App Service + Azure SQL + Azure Cache for Redis +
   Azure Blob + Azure SignalR** and a "deployment-slot runbook." Every one of those is **paid/cloud** and is
   ruled out by `CLAUDE.md` / ADR 0004 (**free-only, no Docker, LOCAL-only, no subscriptions**). The
   **devops-agent is not used on this project** (user preference); its purely-local duties fall to backend-agent.
   Deployment is **plans-only** — the human runs it; **no agent deploys, provisions, or runs `git`**.
2. **A performance-polish stance.** The exit criteria demand Lighthouse/Core-Web-Vitals + accessibility targets
   met, and `REVIEW_BACKLOG.md` triaged so every deferred perf/polish item is explicitly fixed or accepted. Some
   items (response compression, the avatar-ETag re-read, the feed-body SQL truncation) are clear fixes; others
   (OutputCache, `EnableRetryOnFailure`, `ForwardedHeaders`) are **delicate against security or the local
   profile** and need a recorded decision rather than a reflexive change.

## Decision

**Deploy plans-only to the free/local/self-hosted stack (SQL Express, in-memory cache, local `IFileStorage`,
self-hosted SignalR, self-VAPID push), configured entirely by env-vars/user-secrets, with schema changes shipped
as an idempotent EF SQL script applied manually — never auto-migrated. In the performance pass, fix the
BREACH-safe/high-value backlog items outright, and gate the security-delicate ones (OutputCache) behind
measurement + a security-ordering review, keeping the CSP header authoritative on every response.**

### 1. Deployment posture (plans-only, free/local)

- **Hosting/DB:** local/self-hosted; **SQL Server Express** (prod) / **LocalDB** (dev), free, no Docker. **No
  Azure paid tier.** (Azure F1 free tier exists as a documented *option* but is never the default.)
- **Config/secrets:** `ConnectionStrings__Default`, `WebPush:PrivateKey`, `FileStorage:UrlSigningKey`, Google
  creds — **user-secrets (dev) / env-vars (prod)** only, `__` for nesting; **never** in `appsettings.*.json`.
  The VAPID **public** key may sit in appsettings (non-secret). Push config **degrades to off** when absent
  (a startup Warning, mirroring the conditional Google-OAuth wiring) — **never a fatal boot**.
- **Migrations:** regenerate **`artifacts/migrate.sql`** (`dotnet ef migrations script --idempotent`) and apply
  it **manually** (or `scripts/db-update.ps1` in dev). **Never `Database.Migrate()` on startup** (race +
  rollback hazard). The Phase-6 migration is **additive** (ADR 0020 §4 / design §9).
- **`/health`:** the existing anonymous DB-connectivity probe, unchanged. Push/VAPID is **not** a health
  dependency (absence degrades).
- **Data-Protection key ring:** persist to a stable folder (`App_Data/keys`, `PersistKeysToFileSystem`) so auth
  cookies + anti-forgery tokens survive an app restart on the single self-hosted instance.
- **`EnableRetryOnFailure` (backlog A3): stays OFF** — the user-managed registration `IDbContextTransaction` is
  incompatible with SQL retry unless wrapped in an execution strategy (backlog CR6); transient faults are rare on
  the single-instance local/Express profile. Document the coupling; re-enable only with the execution-strategy
  wrap if a flaky remote DB ever appears.
- **`ForwardedHeaders` (backlog 2.5/4.1):** the rate-limit partition keys on `RemoteIpAddress`; **iff** a reverse
  proxy is ever introduced, add `UseForwardedHeaders` + a **trusted-proxy allow-list before `UseRateLimiter`**.
  **Plans-only** — not wired for direct-Kestrel local.
- **Publish:** `dotnet publish -c Release` runs `BuildFrontendAssets` (npm ci + build); the runbook **verifies**
  `./publish/wwwroot/{sw.js,manifest.webmanifest,icons/*,dist/*}` are present. `ASPNETCORE_ENVIRONMENT=Production`
  **spelled verbatim** (the Phase-1 startup guard catches a typo).

### 2. Performance-polish stance (fix the safe/high-value; gate the delicate)

- **Static-asset compression (fix — closes the headline backlog item).** Compress the JS/CSS/font bundle
  (`site-*.js` ~184 KB incl. ~56 KB SignalR → Brotli ~55 KB) via **`MapStaticAssets()`** (build-time
  gzip+Brotli, strong ETag, immutable cache) — **or** `AddResponseCompression` (Brotli+Gzip) **scoped to
  static/compressible MIME types** with `EnableForHttps` on **only for those secret-free types**. **Dynamic-HTML
  compression stays OFF / review-gated** — a compressed HTML response carrying both the rotating anti-forgery
  token and reflected user input is the **BREACH** exposure; static assets carry neither, so the scope captures
  ~all the win with zero BREACH risk.
- **CSP stays authoritative on every response.** `SecurityHeadersMiddleware` remains **upstream** of static
  serving/compression and of any OutputCache, so a compressed asset, a cached hit, or a dynamic page is always
  stamped with the single CSP/`nosniff`/`Referrer-Policy`/`Permissions-Policy` set on the live request. This is
  the exact ordering the Phase-2 backlog flagged.
- **OutputCache (gate on measurement + a security review — do not force).** Phase-2 exit accepted-deferred it
  (the TMDB round-trip is already cached; only the Razor render remains). Re-measure in the perf pass; **iff** the
  render is a measured repeat cost, wire a `DiscoveryRails` policy (short TTL, `VaryByQuery`, **anonymous +
  no-`Set-Cookie` only**, tagged) with a security-agent ordering review (cached hits keep the live CSP header;
  don't let the cache bypass the `public-read` limiter in a budget-harmful way — cache hits spend no TMDB budget,
  so serving them ahead of the limiter is acceptable). Else it stays deferred.
- **Straightforward fixes (close their backlog items):** the **avatar-serving** cheap-validator (304 before a
  full re-read+rehash; backlog 4.1); the **feed-body SQL truncation** (`Body.Substring(0,201)` server-side + fix
  the wrong comment; backlog 3.4); **SignalR lazy-load** off the shared bundle (backlog 3.5); **`CacheLog` query
  redaction** (backlog 2.5); **motion/LCP/CLS** trims + **eager above-the-fold avatars** (backlog 2.3/4.2).
- **Targets/measurement:** LCP < 2.5 s, CLS < 0.1, INP < 200 ms; Lighthouse installable-PWA pass and
  ≥ 90 Perf/A11y/BP/SEO on Home/Details/Feed; measured with **Lighthouse + DevTools + Playwright — all free**.
  No paid perf service.

## Consequences

**Positive**
- Zero-cost, zero-cloud, no-Docker deployment any developer can run locally; agents never touch cloud or `git`.
- Schema changes are auditable, forward-only, manually applied — no startup-migration race.
- The compression win (the biggest Lighthouse lever) ships **BREACH-safe**; the CSP header stays authoritative
  through caching/compression.
- The delicate items (OutputCache) are decided honestly — pulled forward only behind measurement + a security
  review — instead of shipped unreviewed at the ship gate.
- `REVIEW_BACKLOG.md` is triaged to fixed-or-accepted (exit criterion) with a recorded disposition each.

**Negative / accepted**
- Free providers keep their known ceilings (single-instance in-memory cache/SignalR, Windows LocalDB/Express) —
  accepted for the local-first profile; each stays behind its port for a future paid swap.
- Dynamic-HTML compression is deliberately left off (BREACH) — a small missed win on already-small HTML partials.
- `EnableRetryOnFailure` stays off — accepted for the single-instance local target.
- Deployment is plans-only — nothing is verified in a real cloud (out of scope by constraint).

## Alternatives considered

1. **Deploy to Azure App Service + Azure SQL/Redis/Blob/SignalR per the brief.** Rejected — paid/cloud/subscription,
   violates ADR 0004. Free/local stack + plans-only instead.
2. **Auto-apply migrations on startup (`Database.Migrate()`).** Rejected — startup race + hard rollback; ship an
   idempotent script applied manually (skill).
3. **Compress everything including dynamic HTML for max Lighthouse.** Rejected — BREACH exposure on responses
   carrying the anti-forgery token + reflected user input; scope compression to secret-free static assets.
4. **Wire OutputCache unconditionally now.** Rejected — Phase-2 exit accepted-deferred it; forcing it at the ship
   gate risks a CSP/limiter-ordering regression. Gate on measurement + review.
5. **Turn on `EnableRetryOnFailure` for "prod hardening."** Rejected — breaks the user-managed registration
   transaction unless wrapped in an execution strategy; unnecessary for the local single-instance profile.
6. **Use the devops-agent for the runbook/scripts.** Rejected — the user disabled devops-agent on this project;
   backend-agent owns local ops, documentation-agent writes the runbook.

## Related
- ADR 0004 (free/local-only — the substitution table this deployment realizes), ADR 0002 (LocalDB/no-Docker DB),
  ADR 0011 (self-hosted SignalR — no backplane unless scaling), ADR 0013 (avatar serving — the ETag fix here),
  ADR 0019 (publish must include the PWA artifacts; `no-cache` on `sw.js`/manifest), ADR 0020 (VAPID secret
  handling + the additive migration).
- `docs/architecture/phase-6-polish-deployment-design.md` §6 (performance, incl. the REVIEW_BACKLOG closure
  table), §8 (deployment), §9 (migration), §10 (Options).
- Skills: `.claude/skills/azure-deployment/SKILL.md` (read as the free/self-hosted reference),
  `.claude/skills/dotnet-performance/SKILL.md`, `.claude/skills/ef-core-migrations/SKILL.md`,
  `.claude/skills/dotnet-configuration-options/SKILL.md`, `.claude/skills/redis-caching/SKILL.md`.

---

_Design-only ADR authored 2026-07-03 (verified: `Program.cs` has no `MapStaticAssets`/`AddResponseCompression`/
`AddOutputCache`; `SecurityHeadersMiddleware` runs before `UseStaticFiles`; `MapHealthChecks("/health")`
anonymous DB probe; `scripts/db-update.ps1` + `artifacts/migrate.sql` exist; the Phase-1 exact-environment-name
startup guard; the registration `IDbContextTransaction` with `EnableRetryOnFailure` OFF). No application code
written; deployment is plans-only and not executed._

---

## Verification note (2026-07-06) — what shipped in Milestones 6.4 (performance) and 6.6 (deployment readiness)

_Recorded at the Milestone-6.6 documentation pass. This note reconciles the accepted Decision with the
shipped code and the new `docs/deployment/runbook.md`; it **does not** rewrite the Decision/Consequences._

- **Static compression (Decision §2).** Shipped as **`AddResponseCompression` (Brotli + Gzip)** scoped to
  static/compressible MIME types, with `EnableForHttps` on for those secret-free types and dynamic-HTML
  compression left OFF (BREACH-safe). This is the **explicitly-offered fallback** in Decision §2 —
  `MapStaticAssets()` was **deliberately not adopted** because Cinora's custom esbuild → `/dist` pipeline
  does not participate in the static-web-assets manifest. `SecurityHeadersMiddleware` runs **upstream** of
  `UseResponseCompression`/`UseStaticFiles`, so the live CSP header still stamps every response (Decision §2
  ordering, verified in `Program.cs`). `/dist/*` is immutable **only outside Development**; `sw.js` +
  `manifest.webmanifest` are `no-cache`. **No divergence from the accepted decision — recording the arm chosen.**
- **OutputCache (Decision §2, gated).** Remains **deferred** — `Program.cs` has **no** `AddOutputCache`
  (measurement did not force it), consistent with the "gate on measurement" decision.
- **`EnableRetryOnFailure` (Decision §1).** Confirmed **OFF** in `src/Cinora.Infrastructure/DependencyInjection.cs`
  (comment `CR6`); the coupling is documented in the runbook §9.1.
- **`ForwardedHeaders` (Decision §1).** Confirmed **not wired** (`Program.cs`) — plans-only in the runbook §9.2.
- **Migration (Decision §1).** `Phase6PushAndPreferences` is **additive** (4 default-`1` `Users` columns +
  `Devices.EndpointHash` + unique `IX_Devices_UserId_EndpointHash`); `artifacts/migrate.sql` present. The
  runbook §5.3 carries the pre-apply `Devices`-empty check the new unique index requires.
- **Connection-string key (clarification).** The Decision's `ConnectionStrings__Default` shorthand is, in the
  shipped code, the key **`ConnectionStrings:DefaultConnection`** (env var `ConnectionStrings__DefaultConnection`,
  verified in `DependencyInjection.cs` + `Program.cs`). The runbook documents the **code** key.
- **Data-Protection key ring (Decision §1) — shipped.** The persistence wiring is present in
  `src/Cinora.Web/Program.cs` (lines 221-235): `AddDataProtection().PersistKeysToFileSystem(ContentRoot/
  App_Data/keys).SetApplicationName("Cinora")`, guarded `if (!isTesting)` so the `WebApplicationFactory` host
  keeps ephemeral keys (parallel test hosts never share a folder), directory created at startup. Auth cookies +
  anti-forgery tokens now survive an app restart on the single self-hosted instance, satisfying Decision §1. The
  runbook §4 documents verification (`App_Data/keys/key-*.xml` present after first run). **All Decision §1 items
  are now realized in code.**
- **Lighthouse / CWV targets (Decision §2).** Measurement against a `Release` browser run is a **carried manual
  step** (runbook §10) — not agent-run.
- **Runbook.** `docs/deployment/runbook.md` authored at Milestone 6.6 (plans-only; nothing executed).

Last verified against code: 2026-07-06
