import { test, expect } from '@playwright/test';
import { PRECONDITION, isAppReachable, registerNewUser } from './support/app';

/**
 * Flow 5 — NOTIFICATION (design §12 / App_Flow: the notification bell → inbox, plus the settings surface).
 *
 * A fresh account has an empty inbox, so this asserts the bell opens the inbox and its stable empty-state
 * marker renders — a deterministic check that does not require a second user to generate a notification (the
 * full cross-user push round-trip is the design's real-browser 6.2 exit criterion, verified manually).
 *
 * Selectors (read from Components/NotificationBell/Default.cshtml, Notifications/Index.cshtml,
 * Settings/Notifications.cshtml): the nav bell is #notification-bell; the inbox empty state is
 * [data-notifications-empty]; the notification-settings per-type toggles live in #notification-prefs-form.
 */
test.describe('Notification flow', () => {
  test.beforeEach(async ({ request }) => {
    test.skip(!(await isAppReachable(request)), PRECONDITION);
  });

  test('notificationBell_SignedInUser_OpensInboxShowingEmptyState', async ({ page }) => {
    await registerNewUser(page);

    await page.locator('#notification-bell').click();

    await expect(page).toHaveURL(/\/notifications/);
    await expect(page.getByRole('heading', { name: 'Notifications', level: 1 })).toBeVisible();
    await expect(page.locator('[data-notifications-empty]')).toBeVisible();
  });

  test('notificationSettings_SignedInUser_RendersPushAndPerTypeControls', async ({ page }) => {
    await registerNewUser(page);
    await page.goto('/settings/notifications');

    await expect(page.getByRole('heading', { name: 'Notifications', level: 1 })).toBeVisible();
    // The per-type push toggles form is present (in-app notifications always arrive; these govern push).
    await expect(page.locator('#notification-prefs-form')).toBeVisible();
  });
});
