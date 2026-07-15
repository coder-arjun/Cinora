import { test, expect } from '@playwright/test';
import { KNOWN_TITLE, PRECONDITION, isAppReachable, registerNewUser } from './support/app';

/**
 * Flow 3 — REVIEW (design §12 / App_Flow: Movie Details → create review with a rating out of 10).
 *
 * A fresh account is registered per test so the user has NO existing review — the Details page's #my-review
 * region then lazy-loads the write form (_ReviewWriteForm) rather than the edit card. Requires the app's TMDB
 * key (the Details page + the review's EnsureTitleCached seam first-touch the title).
 *
 * Selectors (read from Discovery/Details.cshtml + _ReviewWriteForm.cshtml + _ReviewCard.cshtml): the write
 * form fields are #new-review-rating (name=Rating, 1–10) and #new-review-body (name=Body); on submit HTMX
 * swaps the returned _ReviewCard into #my-review, where the rating renders as an aria-label "Rated N out of 10".
 */
test.describe('Review flow', () => {
  test.beforeEach(async ({ request }) => {
    test.skip(!(await isAppReachable(request)), PRECONDITION);
  });

  test('createReview_SignedInUserPostsRating_ShowsReviewInMyReviewRegion', async ({ page }) => {
    await registerNewUser(page);
    await page.goto(KNOWN_TITLE.path);

    const reviewBody = `A masterpiece — e2e ${Date.now()}`;

    // #my-review lazy-loads the write form via hx-trigger="load"; .fill auto-waits for the field to appear.
    await page.locator('#new-review-rating').fill('9');
    await page.locator('#new-review-body').fill(reviewBody);
    await page.getByRole('button', { name: 'Post review' }).click();

    const myReview = page.locator('#my-review');
    await expect(myReview.locator('[data-review-card]')).toBeVisible();
    await expect(myReview).toContainText(reviewBody);
    // The rating pill's accessible name is the stable, restyle-proof hook for "rated 9 out of 10".
    await expect(myReview.getByRole('img', { name: 'Rated 9 out of 10' })).toBeVisible();
  });
});
