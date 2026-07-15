# Cinora E2E (Playwright) — Phase 6.6

Free/MIT Playwright end-to-end tests for Cinora's five critical flows: **login, search, review, watchlist,
notification** (design `docs/architecture/phase-6-polish-deployment-design.md` §12 / §6.5, ADR 0021). This
project is **isolated** from the `src/Cinora.Web` esbuild frontend build and the xUnit test projects — it has
its own `package.json`, `playwright.config.ts`, and `node_modules`.

> **Authored-but-carried.** The specs are version-controlled and ready to run, but the actual **run needs a
> real browser + a running Release app against a migrated DB** — that is a **carried CI/manual verification
> step** (design §12: "the E2E suite is green" is an exit check the human/CI runs). Do not treat the mere
> presence of these files as a green run.

## Prerequisites

1. **A running Release app.** Start Cinora and note the HTTPS URL (default `https://localhost:7149`, the
   `https` launch profile):
   ```powershell
   # from the repo root
   dotnet run --project src/Cinora.Web --launch-profile https -c Release
   ```
2. **A migrated database** the app can reach. The app never auto-migrates — apply the reviewed idempotent
   script or the helper first:
   ```powershell
   scripts/db-update.ps1            # or: apply artifacts/migrate.sql
   ```
3. **A working TMDB key** in the app's user-secrets (search, Details, review, and watchlist all touch TMDB).
   This is already set + live-verified per `PROGRESS.md`.
4. **Node 18+** (verified against Node 22) and this project's dependencies + the Chromium browser (below).

## Install

```powershell
cd e2e
npm install
npx playwright install chromium
```

## Run

```powershell
# all projects (chromium flow suite + dark-mode & reduced-motion search coverage)
npx playwright test

# just the flow suite
npx playwright test --project=chromium

# override the base URL (e.g. a different port or a deployed test host)
$env:CINORA_BASE_URL = "https://localhost:7149"; npx playwright test

# open the HTML report after a run
npx playwright show-report
```

If the app is **not** reachable at the base URL, every spec **skips gracefully** (a `test.skip` precondition
in each `beforeEach`) instead of red-failing — so a bare `npx playwright test` in an environment without the
app reports *skipped*, not *failed*.

## What runs where

| Project | Scope | Why |
|---|---|---|
| `chromium` | all five flow specs | The full login/search/review/watchlist/notification suite. |
| `dark-mode` | `search.spec.ts` only (`colorScheme: 'dark'`) | Design §6.5 dark-mode coverage on a key flow, without extra account registrations. |
| `reduced-motion` | `search.spec.ts` only (`reducedMotion: 'reduce'`) | Design §6.5 reduced-motion coverage — confirms the flow works with `prefers-reduced-motion: reduce`. |

Config highlights (`playwright.config.ts`): base URL from `CINORA_BASE_URL` (default `https://localhost:7149`),
`ignoreHTTPSErrors: true` for the dev cert, `trace: 'on-first-retry'`, screenshots/video retained on failure.

## Conventions

- **Selectors** prefer stable hooks read from the Razor markup: `id` / `data-*` (`#search-q`,
  `#notification-bell`, `[data-watchlist-control]` + `data-wl-status`, `[data-review-card]`,
  `[data-notifications-empty]`, `#notification-prefs-form`) and role/label locators — never brittle Tailwind
  class chains.
- **Anti-forgery** is handled by filling and submitting the **real** forms (the hidden
  `__RequestVerificationToken` rides along); no manual token plumbing.
- **Auto-waiting only** — web-first `expect(...)` assertions; no `page.waitForTimeout` / `Task.Delay`.
- The authenticated flows **register a fresh unique account per test**, so they are order-independent and
  parallel-safe, and the review/watchlist flows start from a clean per-user state.

## Caveats a first real run should expect

- **Selectors are authored against source, not a live DOM.** The first real run may need small locator
  tweaks (e.g. the watchlist disclosure open/close interaction, or the live-search debounce timing). Use
  `npx playwright test --headed --project=chromium` and `npx playwright show-trace` to adjust.
- **Auth rate limit.** The auth POSTs are guarded by a per-IP `auth` rate-limit policy. Registering many
  accounts quickly (many workers / repeated runs) can trip it and cause flakiness. If so, run
  `npx playwright test --project=chromium --workers=1` (`npm run test:serial`), or raise the limit in the
  `Testing` environment.
- **TMDB / network.** Search, Details, review, and watchlist depend on TMDB being reachable from the running
  app. A TMDB outage will fail those flows (not a suite bug).
- **The full push round-trip** (a second user's action producing an OS notification with a deep link) is the
  design's real-browser **6.2** exit criterion, verified manually — it is out of scope for this deterministic
  suite, which asserts the bell → inbox and the settings surface instead.
