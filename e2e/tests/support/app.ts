import { type APIRequestContext, type Page, expect } from '@playwright/test';

/**
 * Shared E2E helpers for the Cinora flow suite (design §12).
 *
 * Selectors here were authored against the REAL Razor markup (read, not guessed):
 *   - Auth forms (Account/Login.cshtml, Account/Register.cshtml): asp-for tag helpers emit id/name
 *     "Email"/"Password"/"DisplayName"/"ConfirmPassword"; the hidden __RequestVerificationToken is part of
 *     the form, so filling + submitting the real form satisfies anti-forgery with no manual token handling.
 *   - The authenticated nav (_Layout.cshtml) shows a "Log out" submit button + the notification bell
 *     (#notification-bell); anonymous shows "Sign in" / "Register".
 * A first real run may still need selector tweaks (these were read from source, not a live DOM).
 */

/** Satisfies the Identity password policy (≥ 8 chars, mixed) used by RegisterViewModel. */
export const TEST_PASSWORD = 'E2e!Passw0rd123';

/**
 * A very stable TMDB title used by the review + watchlist flows. Fight Club (movie / 550) is one of the
 * longest-lived ids in TMDB. Requires the running app to have a working TMDB key (PROGRESS: set +
 * live-verified in user-secrets). The Details route grammar `/discover/title/{media}/{tmdbId}` is fixed
 * across the codebase (see _TitleCard.cshtml).
 */
export const KNOWN_TITLE = {
  media: 'movie',
  tmdbId: 550,
  path: '/discover/title/movie/550',
} as const;

/** A collision-resistant test email so every registration is a brand-new account (order-independent). */
export function uniqueEmail(prefix = 'e2e'): string {
  const rand = Math.random().toString(36).slice(2, 8);
  return `${prefix}.${Date.now()}.${rand}@cinora.test`;
}

/**
 * Best-effort reachability probe. Every spec's beforeEach uses this to SKIP (not fail) when no Release app
 * is listening at baseURL — this suite is authored-but-carried; the real run needs a running app + a
 * migrated DB. A graceful skip keeps `npx playwright test` from red-failing in an environment without the app.
 */
export async function isAppReachable(request: APIRequestContext): Promise<boolean> {
  try {
    const res = await request.get('/', { failOnStatusCode: false, timeout: 5_000 });
    // Any HTTP response (even a redirect/401) proves the app is up; a 5xx or a thrown error means "not ready".
    return res.status() < 500;
  } catch {
    return false;
  }
}

/**
 * The shared precondition message a spec passes to `test.skip(!reachable, PRECONDITION)` in its beforeEach,
 * so a run without a live app skips gracefully instead of red-failing.
 */
export const PRECONDITION =
  'Cinora app not reachable at baseURL — start a Release app against a migrated DB (see e2e/README.md). ' +
  'This suite is authored-but-carried.';

export interface TestUser {
  email: string;
  displayName: string;
  password: string;
}

/**
 * Registers a brand-new account through the real form and lands authenticated. Registration signs the new
 * user in server-side and redirects to /home, so we assert the authenticated nav ("Log out") rather than a
 * specific URL. Anti-forgery is handled implicitly by submitting the real form.
 */
export async function registerNewUser(page: Page): Promise<TestUser> {
  const user: TestUser = {
    email: uniqueEmail(),
    displayName: `E2E ${Date.now().toString().slice(-6)}`,
    password: TEST_PASSWORD,
  };

  await page.goto('/account/register');
  await page.locator('#DisplayName').fill(user.displayName);
  await page.locator('#Email').fill(user.email);
  await page.locator('#Password').fill(user.password);
  await page.locator('#ConfirmPassword').fill(user.password);
  await page.getByRole('button', { name: 'Create account' }).click();

  await expect(
    page.getByRole('button', { name: 'Log out' }),
    'registration should sign the new user in (authenticated nav shows "Log out")',
  ).toBeVisible();

  return user;
}

/** Signs an existing user in through the real login form (anti-forgery handled by submitting the form). */
export async function login(page: Page, email: string, password: string): Promise<void> {
  await page.goto('/account/login');
  await page.locator('#Email').fill(email);
  await page.locator('#Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();

  await expect(
    page.getByRole('button', { name: 'Log out' }),
    'valid credentials should authenticate (authenticated nav shows "Log out")',
  ).toBeVisible();
}

/** Signs the current user out (the nav POST form) and confirms the public nav returned. */
export async function logout(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Log out' }).click();
  await expect(page.getByRole('link', { name: 'Register' })).toBeVisible();
}
