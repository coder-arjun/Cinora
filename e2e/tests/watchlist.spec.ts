import { test, expect } from '@playwright/test';
import { KNOWN_TITLE, PRECONDITION, isAppReachable, registerNewUser } from './support/app';

/**
 * Flow 4 — WATCHLIST (design §12: add a title to the watchlist, then remove it).
 *
 * A fresh account per test starts with the title not in any list, so the control begins in its
 * "Add to watchlist" state. Requires the app's TMDB key (the set POST first-touches the title via the
 * EnsureTitleCached seam).
 *
 * Selectors (read from _WatchlistControl.cshtml): the control root carries [data-watchlist-control] and a
 * data-wl-status attribute mirroring the WatchlistStatus enum name (PlanToWatch / Watching / Watched, absent
 * when not in a list). The disclosure trigger is [data-wl-trigger]; the three status options are
 * role="menuitemradio"; "Remove from watchlist" is a role="menuitem" (JS-revealed). The control swaps itself
 * (outerHTML) after each write.
 */
test.describe('Watchlist flow', () => {
  test.beforeEach(async ({ request }) => {
    test.skip(!(await isAppReachable(request)), PRECONDITION);
  });

  test('watchlist_AddThenRemove_TogglesTheControlStatus', async ({ page }) => {
    await registerNewUser(page);
    await page.goto(KNOWN_TITLE.path);

    const control = page.locator('[data-watchlist-control]');
    await expect(control).toBeVisible();
    // Fresh user → not in any list yet: the trigger's accessible name is "Add to watchlist".
    await expect(page.getByLabel('Add to watchlist')).toBeVisible();

    // Open the disclosure, choose "Plan to Watch"; the control re-renders with data-wl-status="PlanToWatch".
    await page.locator('[data-wl-trigger]').click();
    await page.getByRole('menuitemradio', { name: 'Plan to Watch' }).click();
    await expect(page.locator('[data-watchlist-control]')).toHaveAttribute('data-wl-status', 'PlanToWatch');

    // Reopen and remove; the control returns to the "Add to watchlist" state (data-wl-status attribute gone).
    await page.locator('[data-wl-trigger]').click();
    await page.getByRole('menuitem', { name: 'Remove from watchlist' }).click();
    await expect(page.getByLabel('Add to watchlist')).toBeVisible();
    await expect(page.locator('[data-watchlist-control]')).not.toHaveAttribute(
      'data-wl-status',
      'PlanToWatch',
    );
  });
});
