import { test, expect } from '@playwright/test';
import { PRECONDITION, isAppReachable, login, logout, registerNewUser } from './support/app';

/**
 * Flow 1 — LOGIN (design §12 / App_Flow: Landing → Login/Register → authenticated app).
 *
 * Covers register (which signs the new user in), sign-out, sign-in again, and the generic invalid-credential
 * message. Anti-forgery is exercised for real: every POST goes through the actual form, whose hidden
 * __RequestVerificationToken is submitted by the browser.
 */
test.describe('Authentication flow', () => {
  test.beforeEach(async ({ request }) => {
    test.skip(!(await isAppReachable(request)), PRECONDITION);
  });

  test('register_NewAccount_SignsInAndReachesAuthenticatedApp', async ({ page }) => {
    await registerNewUser(page);

    // The notification bell renders only for signed-in users (_Layout authed branch), so its presence
    // confirms we are authenticated after registration.
    await expect(page.locator('#notification-bell')).toBeVisible();
  });

  test('login_AfterLogoutWithValidCredentials_ReauthenticatesUser', async ({ page }) => {
    const user = await registerNewUser(page);

    await logout(page);
    await login(page, user.email, user.password);

    await expect(page.locator('#notification-bell')).toBeVisible();
  });

  test('login_WithInvalidCredentials_ShowsGenericError', async ({ page }) => {
    await page.goto('/account/login');
    await page.locator('#Email').fill('nobody@cinora.test');
    await page.locator('#Password').fill('definitely-wrong-123');
    await page.getByRole('button', { name: 'Sign in' }).click();

    // AccountController returns one generic message for every credential failure (never reveals existence).
    await expect(page.getByText('Invalid login attempt.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Log out' })).toHaveCount(0);
  });
});
