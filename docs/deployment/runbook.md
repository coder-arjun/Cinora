# Cinora — Deployment Runbook (free / local / self-hosted)

> **Status:** Plans-only. **Nothing in this document is executed by an agent.** Every command
> below is written for a **human** to run, by hand, on their own machine. Cinora deploys to a
> **free, local, self-hosted** stack — **no Docker, no cloud subscription, no paid Azure tier**
> (ADR 0004, ADR 0021).
>
> **Phase:** 6 (Polish & Deployment), Milestone 6.6.
> **Related design:** `docs/architecture/phase-6-polish-deployment-design.md` §8 (authoritative),
> §9 (the additive migration), §10 (Options/secrets).
> **Related ADRs:** [0021](../adr/0021-deployment-posture-and-performance-polish.md) (deployment
> posture + performance-polish stance), [0019](../adr/0019-pwa-service-worker-caching-and-update.md)
> (PWA publish artifacts + `no-cache` headers), [0020](../adr/0020-web-push-and-notification-preferences.md)
> (VAPID secret handling + the additive migration).

---

## 0. Overview

- **Free / local / self-hosted only.** No Docker, no cloud provider, no subscription, no paid
  service. Everything runs on the target machine with free/open tooling.
- **The human runs everything here.** Agents never deploy, never provision, never run `git`, and
  never auto-apply migrations. This runbook is instructions, not automation.
- **Toolchain** (see also the [README](../../README.md) prerequisites and `global.json`):
  - **.NET SDK 10.0.x** — `global.json` pins `10.0.301` (`rollForward: latestFeature`).
  - **Node.js 20+** (Node 22 recommended) + npm — drives the Tailwind v4 + TypeScript asset build.
  - **SQL Server** — free editions only (see §2).
  - **`dotnet-ef` global tool 10.0.x** — only needed to regenerate/apply migrations
    (`dotnet tool install --global dotnet-ef`).
  - **PowerShell 7+** — for the `scripts/` helpers.
- **What ships in Phase 6** that this runbook must account for: an installable PWA (service worker +
  manifest + offline), Web Push (self-generated VAPID), notification preferences, and the
  performance-polish pass. Push is **strictly additive** — the app runs fully without it (§3.1).

---

## 1. Build and test before you deploy

Run from the repository root. `TreatWarningsAsErrors` is ON — a warning fails the build.

```powershell
dotnet build Cinora.sln -c Release
dotnet test  Cinora.sln -c Release
```

Integration tests run against a dedicated LocalDB database (`CinoraTest`), created and migrated
automatically; they never touch your development or production data.

---

## 2. Database targets (free, no Docker)

| Environment | Engine | Instance / database | How the connection string is supplied |
|---|---|---|---|
| **Development** | SQL Server (local default instance, Windows auth) | `Server=localhost` → `CinoraDev` | `appsettings.Development.json` `ConnectionStrings:DefaultConnection` (dev-only, non-secret local string) |
| **Integration tests** | SQL Server **LocalDB** | `(localdb)\MSSQLLocalDB` → `CinoraTest` | Set by the test host; created + migrated automatically |
| **Production** | SQL Server **Express** (free) | your instance → e.g. `CinoraProd` | `ConnectionStrings__DefaultConnection` **environment variable** — **never** `appsettings.*.json` |

The application reads the connection string from the configuration key
**`ConnectionStrings:DefaultConnection`** (verified in `src/Cinora.Infrastructure/DependencyInjection.cs`
and reused for Hangfire storage in `src/Cinora.Web/Program.cs`). If it is missing the app fails fast
at startup with a clear message rather than erroring on first DB use.

> **Environment-variable nesting.** On Linux (and anywhere env vars carry configuration), the `:`
> section separator is written as a double underscore `__`. The production connection string is the
> environment variable **`ConnectionStrings__DefaultConnection`**.

```powershell
# Production example (PowerShell) — set the connection string as an environment variable, not in appsettings.
$env:ConnectionStrings__DefaultConnection = "Server=.\SQLEXPRESS;Database=CinoraProd;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True"
```

```bash
# Production example (Linux / bash) — note the __ nesting.
export ConnectionStrings__DefaultConnection="Server=localhost;Database=CinoraProd;User Id=cinora;Password=...;Encrypt=True;TrustServerCertificate=True"
```

---

## 3. Secrets per environment (user-secrets in dev / environment variables in prod)

**No secret ever goes in `appsettings.*.json`.** In development use `dotnet user-secrets`; in
production use environment variables (with `__` nesting). appsettings holds only non-secrets.

| Secret | Configuration key | Secret? | Behavior if absent |
|---|---|---|---|
| VAPID **private** key | `WebPush:PrivateKey` | **Yes** — user-secrets/env only | Push disabled (degrades, §3.1) |
| VAPID public key | `WebPush:PublicKey` | No (may live in appsettings) | Push disabled |
| VAPID subject | `WebPush:Subject` | No (`mailto:` / `https://` URL) | Push disabled |
| File-URL signing key | `FileStorage:UrlSigningKey` | **Yes** — user-secrets/env only | **Production: startup aborts** (this key is required). Dev/Testing: an ephemeral key is auto-generated |
| Google OAuth client id | `GoogleAuth:ClientId` | **Yes** — user-secrets/env only | Google sign-in disabled; email/password still works |
| Google OAuth client secret | `GoogleAuth:ClientSecret` | **Yes** — user-secrets/env only | Google sign-in disabled |

> **Required vs. degrade-if-absent.** `FileStorage:UrlSigningKey` is **required in Production** — a missing
> value **aborts startup** (`FileStorageProductionValidateOptions`), so set it before deploying; in
> Development/Testing an ephemeral key is auto-generated. By contrast, **Web Push (`WebPush:*`) and Google
> OAuth are optional everywhere** — absent, those features simply stay off and the app boots normally.

Development (user-secrets — free; scoped to `src/Cinora.Web`):

```powershell
dotnet user-secrets set "WebPush:Subject"    "mailto:admin@your-domain.example" --project src/Cinora.Web
dotnet user-secrets set "WebPush:PublicKey"  "<vapid-public-key-base64url>"      --project src/Cinora.Web
dotnet user-secrets set "WebPush:PrivateKey" "<vapid-private-key-base64url>"     --project src/Cinora.Web
dotnet user-secrets set "FileStorage:UrlSigningKey" "<random-32+-byte-base64>"  --project src/Cinora.Web
dotnet user-secrets set "GoogleAuth:ClientId"     "<google-client-id>"          --project src/Cinora.Web
dotnet user-secrets set "GoogleAuth:ClientSecret" "<google-client-secret>"      --project src/Cinora.Web
```

Production (environment variables — PowerShell shown; use `__` nesting on Linux):

```powershell
$env:WebPush__Subject          = "mailto:admin@your-domain.example"
$env:WebPush__PublicKey        = "<vapid-public-key-base64url>"
$env:WebPush__PrivateKey       = "<vapid-private-key-base64url>"   # SECRET
$env:FileStorage__UrlSigningKey = "<random-32+-byte-base64>"       # SECRET
$env:GoogleAuth__ClientId      = "<google-client-id>"              # SECRET
$env:GoogleAuth__ClientSecret  = "<google-client-secret>"          # SECRET
```

### 3.1 VAPID keys and the "push degrades, never crashes" contract

Web Push is **additive**. All three `WebPush` fields are **optional**: if any is absent, the app
still boots and delivers **in-app** notifications (inbox + SignalR live toast) exactly as before —
Web Push simply stays **OFF** (`WebPushOptions.IsConfigured` is `false`, `WebPushSender` no-ops, and
`GET /push/public-key` reports unconfigured). This is verified in
`src/Cinora.Infrastructure/Options/WebPushOptions.cs` and
`src/Cinora.Infrastructure/DependencyInjection.cs`.

> **The degrade signal — shipped.** The design (§8) describes an unconfigured VAPID setup as "push
> disabled" via a **startup Warning**, and that is what ships: `src/Cinora.Web/Program.cs` (lines
> 403-413) emits a one-shot `Log.Warning("Web Push is DISABLED — no VAPID keys configured …")` at
> startup when `WebPushOptions.IsConfigured == false` (mirroring the environment-name guard in §7), so
> a deployer sees the disabled state in the boot log. At runtime the sender additionally no-ops each
> send (a per-send **Debug**-level log, `WebPushSender.LogPushDisabled`). The operational contract —
> **absence degrades push to off, it never crashes boot** — holds. A malformed value that *is* present
> still fails validation (the `WebPushOptions` data-annotation shape checks).

**Generate a VAPID key pair (one-off, on the human's machine).** The keys are a standard
P-256/base64url pair. Two free ways:

- Using the repo's already-pinned `WebPush` library, from a throwaway console/script:

  ```csharp
  // dotnet run in a scratch console that references the WebPush package (already pinned in this repo)
  var keys = WebPush.VapidHelper.GenerateVapidKeys();
  System.Console.WriteLine($"Public : {keys.PublicKey}");
  System.Console.WriteLine($"Private: {keys.PrivateKey}");   // treat as a secret
  ```

- Or the free `web-push` npm CLI (no project dependency added):

  ```bash
  npx web-push generate-vapid-keys
  ```

Store the **private** key as a secret (user-secrets/env); the **public** key is non-secret and may
live in appsettings or a secret store. **Rotating the VAPID keys invalidates every existing push
subscription** — clients simply re-subscribe next time they open the app.

---

## 4. Data-Protection key ring (operational requirement)

ASP.NET Core Data Protection encrypts the **authentication cookie** and the **anti-forgery tokens**.
On a single self-hosted instance the key ring **must persist to a stable folder** so those survive an
app restart — otherwise every restart rotates the keys and **invalidates every active session and
anti-forgery token** (all users are logged out and in-flight forms break).

**Requirement:** persist the key ring to a stable path such as `App_Data/keys`
(`PersistKeysToFileSystem`), on durable storage the app account can read/write, backed up with the
app.

> **✅ Shipped (Milestone 6.6).** The key-ring persistence **is wired** in `src/Cinora.Web/Program.cs`
> (lines 221-235): `AddDataProtection().PersistKeysToFileSystem(ContentRoot/App_Data/keys)
> .SetApplicationName("Cinora")`, guarded `if (!isTesting)` so the integration-test `WebApplicationFactory`
> host keeps ephemeral keys (parallel test hosts never share a key folder). The directory is created at
> startup. So a production restart now **preserves** existing sessions and anti-forgery tokens — the
> requirement below is satisfied by shipped code, not a pending task.

**How to verify:** after the app has started at least once, confirm the key-ring XML files exist under
the configured folder:

```powershell
Get-ChildItem .\publish\App_Data\keys    # expect one or more key-*.xml files
```

---

## 5. Migrations — idempotent script, **never** auto-migrate

The app **never** calls `Database.Migrate()` on startup (startup race + hard-to-roll-back). Schema
changes ship as a **reviewed, idempotent SQL script** applied **manually** (or via
`scripts/db-update.ps1` in dev).

### 5.1 Regenerate the idempotent script

```powershell
dotnet ef migrations script --idempotent `
  --project src/Cinora.Infrastructure --startup-project src/Cinora.Web `
  -o artifacts/migrate.sql
```

(`artifacts/migrate.sql` is checked in and reviewed; regenerate it after adding a migration.)

### 5.2 The Phase-6 migration is additive

`Phase6PushAndPreferences` (`src/Cinora.Infrastructure/Migrations/20260706095548_Phase6PushAndPreferences.cs`)
is **forward-only and additive** — no drops, no renames:

- 4 × `bit NOT NULL DEFAULT 1` columns on `Users`
  (`PushFriendRequests`, `PushFriendAccepted`, `PushReviewLikes`, `PushComments`) — the
  notification-preference model; default-on backfills existing users to opt-in.
- `EndpointHash char(64)` column on `Devices` + a **unique index `IX_Devices_UserId_EndpointHash`**
  on `(UserId, EndpointHash)` — the subscribe upsert/dedup key.

### 5.3 ⚠️ Pre-apply check — the `Devices` unique index

The new column is added with `DEFAULT ''`, so **every existing `Devices` row gets the same
`EndpointHash` (`''`)**. If a single user already has **two or more** `Devices` rows, the new
**unique** index `(UserId, EndpointHash)` will collide and the apply will **fail**.

Before applying the migration, verify `Devices` is empty (or holds at most one row per user):

```sql
-- expect 0 (or no user with a count > 1)
SELECT UserId, COUNT(*) AS n FROM dbo.Devices GROUP BY UserId HAVING COUNT(*) > 1;
```

**Truncating `Devices` is safe** — clients simply re-subscribe on their next visit (subscriptions are
re-created from the browser). If in doubt on a fresh prod (push is new — there are no live
subscriptions yet), clear the table first:

```sql
TRUNCATE TABLE dbo.Devices;
```

### 5.4 Apply the script

- **Development:** `scripts/db-update.ps1` (idempotent; targets `Server=localhost`, `CinoraDev`).
- **Production:** apply the reviewed `artifacts/migrate.sql` with your SQL client of choice
  (e.g. `sqlcmd -S <server> -d <db> -i artifacts/migrate.sql`), after the §5.3 pre-apply check.

> **Standing caveat.** `CinoraDev` (and any `Server=localhost` Express instance) has repeatedly been
> unreachable in recent sessions, so it may lag behind the migrations already applied to the
> integration-test DB `CinoraTest`. Apply `artifacts/migrate.sql` / `scripts/db-update.ps1` when the
> instance is up. The app **never** auto-migrates to close the gap for you.

---

## 6. Publish + verify the PWA artifacts

```powershell
dotnet publish src/Cinora.Web -c Release -o ./publish
```

The `BuildFrontendAssets` MSBuild target (in `src/Cinora.Web/Cinora.Web.csproj`) runs `npm ci` then
`npm run build` **before** the SDK collects static web assets on publish, so `./publish` always ships
freshly-built, fingerprinted CSS/JS. **Verify** the published `wwwroot` contains the PWA artifacts
(a common miss — an incomplete publish silently breaks installability/offline):

```powershell
# Expect: dist/ (hashed CSS/JS + manifest.json), sw.js, manifest.webmanifest, and the three icons.
Get-ChildItem .\publish\wwwroot\dist\manifest.json
Get-ChildItem .\publish\wwwroot\sw.js
Get-ChildItem .\publish\wwwroot\manifest.webmanifest
Get-ChildItem .\publish\wwwroot\icons\    # icon-192.png, icon-512.png, icon-maskable-512.png
```

`sw.js` and `manifest.webmanifest` are served with `Cache-Control: no-cache` (revalidate every load
so a new deploy is detected promptly); the content-hashed `/dist/*` assets they point at get
long-lived immutable caching. These are cache/content-type headers only — **not** a CSP change
(ADR 0019).

---

## 7. Run it — environment-name exactness

Production **must** set the environment name **verbatim**:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Production"    # exactly "Production"
```

The app keys behavior off `Development` / `Testing` / `Production` **spelled exactly**. A typo such as
`Prod` is treated as an unknown environment: it logs a **startup Warning** (non-fatal, so the mistake
is visible) and would leave the Development/Testing-only diagnostics endpoints exposed. Bind the app
to its URLs via `ASPNETCORE_URLS` (or Kestrel config); the app logs the bound addresses on startup.

---

## 8. Health check

The anonymous `GET /health` endpoint (`MapHealthChecks("/health").AllowAnonymous()`) is a
DB-connectivity probe backed by `DatabaseHealthCheck` (EF Core `CanConnectAsync`):

- **200 `Healthy`** — the database is reachable.
- **503 `Unhealthy`** — the database is not reachable.

Push / VAPID configuration is **not** a health dependency — an unconfigured push setup does not make
`/health` report unhealthy (push degrades independently, §3.1). In dev the endpoint is reachable at
`http://localhost:5148/health`; in production it is served at the app's bound address under `/health`.

---

## 9. Operational couplings to know

### 9.1 `EnableRetryOnFailure` stays **OFF**

The DbContext is registered **without** `EnableRetryOnFailure`
(`src/Cinora.Infrastructure/DependencyInjection.cs`, comment `CR6`). Registration opens a
**user-initiated transaction** (`IDbContextTransaction`) spanning `UserManager.CreateAsync` + the
domain-user insert; a SQL retrying execution strategy throws on user-initiated transactions unless
those writes are wrapped in `Database.CreateExecutionStrategy().ExecuteAsync(...)`. On the
single-instance local/Express profile transient faults are rare, so retries stay off. **Do not enable
`EnableRetryOnFailure` without first wrapping the registration writes in an execution strategy.**

### 9.2 `ForwardedHeaders` — plans-only, only if a reverse proxy is introduced

The rate-limit partitions key on `RemoteIpAddress`. The direct-Kestrel local target needs no proxy
handling, so `UseForwardedHeaders` is **not wired** (confirmed absent from `Program.cs`). **If** a
reverse proxy (nginx, IIS, etc.) is ever placed in front of the app, add `UseForwardedHeaders` **with
a trusted-proxy allow-list**, **before** `UseRateLimiter` — otherwise every client collapses into a
single rate-limit bucket (the proxy's IP). This remains a plan; do not wire it for direct-Kestrel.

---

## 10. Manual verification checklist (the human's final sign-off)

These require a **real browser**, a **live local model**, or Lighthouse — they were **not** run by any
agent and are the deployer's responsibility before declaring the release good. All tooling is
free/local.

- [ ] **5.5 — Live AI recommendations.** Start Ollama (`ollama serve`) and pull a model
      (`ollama pull llama3.2`); trigger the recommendation precompute from the admin `/jobs` page and
      confirm a grounded, non-hallucinated For-You rail appears.
- [ ] **6.1 — PWA install / offline / update.** In Chrome DevTools **Application** panel against a
      `Release` run: the manifest is valid and the app is **installable**; `sw.js` registers at scope
      `/`; going **Offline** on a previously-visited `/discover` serves from cache, and an uncached
      route serves the designed **`/offline`** page; an authenticated `/home` shows `/offline` offline
      (never a cached personalized page); a rebuilt bundle produces a **new SW version** and the
      **update toast** → a **single reload** adopts it; the **maskable icon** renders correctly.
- [ ] **6.2 — Real push round-trip.** In a real browser, enable push in `/settings/notifications`; from
      a **second** user, like your review; confirm the **OS notification** appears and **clicking it
      deep-links** to the correct Details page (focuses an existing window if one is open).
- [ ] **6.3 — Push settings + permission states.** Verify the permission prompt only appears on a
      deliberate gesture; **denied** and **blocked** states degrade gracefully (in-app still works); a
      **muted** notification type is **not** pushed but **still lands in the in-app inbox**.
- [ ] **6.4 / 6.6 — Lighthouse / Core Web Vitals.** On `/discover`, a Details page, and `/home` in a
      `Release` run: **LCP < 2.5 s**, **CLS < 0.1**, **INP < 200 ms**, and Lighthouse
      **Performance / Accessibility / Best-Practices / SEO ≥ 90** (PWA installable = pass).
      Measure with Lighthouse (`npx lighthouse <url> --preset=desktop --view`) + DevTools + a
      Playwright trace for INP.
- [ ] **6.5 — Accessibility / motion.** A full keyboard walkthrough (landing → login → discover →
      search → details → review → feed → notifications → settings) reaches every control with **no
      focus trap**; `prefers-reduced-motion` disables the polish animations; View Transitions behave;
      the confirm dialogs trap focus correctly; an offline write surfaces the network-error toast.

---

## 11. What this runbook deliberately does **not** do

- No Docker, no cloud provisioning, no managed Azure/AWS service, no paid tier.
- No `git` (all version control is the human's).
- No `Database.Migrate()` on startup — schema changes are the reviewed idempotent script, applied by
  hand.
- Nothing here is executed by an agent — this is a plan for a person to follow.

---

_Last verified against code: 2026-07-06 (verified: `ConnectionStrings:DefaultConnection` key in
`src/Cinora.Infrastructure/DependencyInjection.cs` + `src/Cinora.Web/Program.cs`; `EnableRetryOnFailure`
off with the `CR6` comment; `WebPushOptions` all-optional degrade-to-off with `IsConfigured`;
`Phase6PushAndPreferences` additive migration — 4 default-`1` `Users` columns + `Devices.EndpointHash`
+ unique `IX_Devices_UserId_EndpointHash`; `artifacts/migrate.sql` present; `MapHealthChecks("/health")
.AllowAnonymous()` DB probe; `sw.js` + `manifest.webmanifest` + `icons/{icon-192,icon-512,icon-maskable-512}.png`
present under `wwwroot`; `BuildFrontendAssets` target runs `npm ci` + `npm run build` before static-asset
collection on publish; `AddDataProtection().PersistKeysToFileSystem(App_Data/keys)` **wired** in `Program.cs`
(lines 221-235, skipped under Testing) and the absent-VAPID **startup Warning** (lines 403-413) both shipped;
`UseForwardedHeaders` remains **not** wired — plans-only for direct-Kestrel). Plans-only; nothing executed._
