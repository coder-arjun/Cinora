import { defineConfig, devices } from '@playwright/test';

/**
 * Cinora Phase 6.6 Playwright E2E config (design §12, ADR 0021).
 *
 * The suite targets a **Release-run** app that must be started manually (or by CI) against a migrated
 * database — see e2e/README.md. Nothing here starts the app: the run is a *carried* CI/manual step.
 *
 * Base URL is env-overridable; the default matches the `https` launch profile
 * (src/Cinora.Web/Properties/launchSettings.json → https://localhost:7149). `ignoreHTTPSErrors` accepts the
 * ASP.NET Core dev certificate. Three projects give the design's dark-mode + reduced-motion coverage on top
 * of the default chromium flow suite.
 */
const baseURL = process.env.CINORA_BASE_URL ?? 'https://localhost:7149';

export default defineConfig({
  testDir: './tests',
  // Flows are order-independent and use unique accounts, so parallel-safe. See README for the auth
  // rate-limit caveat if a real run trips the per-IP "auth" policy (drop to --workers=1).
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: process.env.CI ? 2 : undefined,
  timeout: 60_000,
  expect: { timeout: 15_000 },
  reporter: [['list'], ['html', { open: 'never' }]],

  use: {
    baseURL,
    // The dev cert is self-signed; accept it (design §12 / the constraint's ignoreHTTPSErrors requirement).
    ignoreHTTPSErrors: true,
    // Capture a trace only when a test is retried — cheap on green, diagnostic on flake.
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    actionTimeout: 15_000,
    navigationTimeout: 30_000,
  },

  projects: [
    // The full flow suite (login, search, review, watchlist, notification) runs here once.
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
    // Dark-mode coverage (design §6.5). Scoped to the anonymous search flow so it exercises rendering under
    // prefers-color-scheme: dark WITHOUT re-registering accounts (keeps auth rate-limit pressure off). Widen
    // `testMatch` in CI if you want more surfaces screenshotted in dark mode.
    {
      name: 'dark-mode',
      testMatch: /search\.spec\.ts/,
      use: { ...devices['Desktop Chrome'], colorScheme: 'dark' },
    },
    // Reduced-motion coverage (design §6.5). Same scope + rationale: confirms the key flow works with
    // prefers-reduced-motion: reduce (the polish pass must honor it — no animation-gated interactions break).
    {
      name: 'reduced-motion',
      testMatch: /search\.spec\.ts/,
      use: { ...devices['Desktop Chrome'], reducedMotion: 'reduce' },
    },
  ],

  // OPTIONAL local auto-start (kept OFF by design — the prerequisite is a Release app + migrated DB, a
  // carried step). To let Playwright launch it, uncomment and ensure the DB is migrated + TMDB key configured:
  //
  // webServer: {
  //   command: 'dotnet run --project ../src/Cinora.Web --launch-profile https -c Release',
  //   url: baseURL,
  //   reuseExistingServer: true,
  //   ignoreHTTPSErrors: true,
  //   timeout: 180_000,
  // },
});
