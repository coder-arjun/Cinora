import { test, expect } from '@playwright/test';
import { PRECONDITION, isAppReachable } from './support/app';

/**
 * Flow 2 — SEARCH (design §12 / App_Flow: Search → result cards → Movie Details).
 *
 * Anonymous flow (/discover/search is [AllowAnonymous]) — no registration, so this is the spec the dark-mode
 * and reduced-motion projects also run (see playwright.config.ts). Requires the running app's TMDB key to be
 * configured, since search hits TMDB.
 *
 * Selectors (read from Discovery/Search.cshtml + _TitleCard.cshtml): the box is #search-q (name=q), results
 * land in #search-results, and each result is a link whose href matches the FIXED /discover/title/ grammar.
 */
test.describe('Search flow', () => {
  test.beforeEach(async ({ request }) => {
    test.skip(!(await isAppReachable(request)), PRECONDITION);
  });

  test('search_DeepLinkQuery_RendersResultCardsLinkingToDetails', async ({ page }) => {
    // The server renders page 1 into #search-results for a deep-linked query (no-JS baseline), so this does
    // not depend on the debounced live-search JS.
    await page.goto('/discover/search?q=Fight+Club');

    const firstCard = page.locator('#search-results a[href*="/discover/title/"]').first();
    await expect(firstCard).toBeVisible();
  });

  test('search_ClickingResult_NavigatesToTitleDetails', async ({ page }) => {
    await page.goto('/discover/search?q=Fight+Club');

    const firstCard = page.locator('#search-results a[href*="/discover/title/"]').first();
    await expect(firstCard).toBeVisible();
    await firstCard.click();

    await expect(page).toHaveURL(/\/discover\/title\//);
    // The Details hero renders the title as the page h1.
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
  });

  test('search_TypingInSearchBox_UpdatesResultsLive', async ({ page }) => {
    // Enter from the nav search entry point (the empty box / idle prompt).
    await page.goto('/discover/search');
    await page.locator('#search-q').fill('Inception');

    // The debounced Alpine/HTMX live search swaps #search-results; the web-first assertion auto-waits for it
    // (no Task.Delay / waitForTimeout — banned by the skill).
    await expect(page.locator('#search-results a[href*="/discover/title/"]').first()).toBeVisible();
  });
});
