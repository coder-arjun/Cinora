---
name: playwright-e2e
description: Use when writing or stabilizing Playwright end-to-end tests for Cinora — login, search, review, watchlist or notification flows, flaky selectors, page object structure, trace debugging, or dark-mode visual screenshots.
---

# Playwright E2E

## Overview
E2E covers only the critical user journeys (login, search movie, write review, add to watchlist, receive notification) — everything else belongs in cheaper test layers. Determinism comes from `data-testid` selectors and auto-waiting, never sleeps.

## Quick Reference
| Task | Approach |
|---|---|
| Selectors | `getByTestId` / `getByRole` — never Tailwind utility classes |
| Waiting | Web-first assertions (`expect(...).toBeVisible()`); `waitForTimeout` is banned |
| Structure | Page object per screen; tests read as user intent |
| Auth | Log in once in global setup, reuse `storageState` |
| App under test | Local Kestrel via `webServer: { command: 'dotnet run --project src/Cinora.Web' }` |
| Debugging | `trace: 'on-first-retry'`; inspect with `npx playwright show-trace` |
| Visual sanity | `toHaveScreenshot` on key dark-mode pages, animations disabled, fixed viewport |

## Pattern
```ts
// pages/review.page.ts
import { type Page } from '@playwright/test';

export class ReviewPage {
  constructor(private readonly page: Page) {}

  async writeReview(stars: number, text: string) {
    // WHY: data-testid survives Tailwind class churn and copy rewrites;
    // CSS-class selectors rot with every restyle
    await this.page.getByTestId(`star-${stars}`).check();
    await this.page.getByTestId('review-body').fill(text);
    await this.page.getByTestId('review-submit').click();
  }
}

// tests/review.spec.ts
import { test, expect } from '@playwright/test';
import { ReviewPage } from '../pages/review.page';

test('signed-in user writes a review', async ({ page }) => {
  // WHY: storageState from global setup means no login steps here —
  // faster and one fewer flaky dependency per test
  await page.goto('/movies/dune-part-two');

  await new ReviewPage(page).writeReview(9, 'A masterpiece.');

  // WHY: web-first assertion retries until the HTMX swap lands — no sleep needed
  await expect(page.getByTestId('review-list')).toContainText('A masterpiece.');
});
```

Add `data-testid` attributes in the Razor markup as part of feature work, not retrofitted during test-writing. Server-side behavior (validation, authorization, persistence) is cheaper to verify per `.claude/skills/integration-testing/SKILL.md`. Keyboard-only journeys double as accessibility checks — see `.claude/skills/responsive-accessibility/SKILL.md`; HTMX swap timing quirks are covered in `.claude/skills/alpine-htmx-interactivity/SKILL.md`.

## Common Mistakes
| Mistake | Fix |
|---|---|
| `page.waitForTimeout(2000)` | Replace with a web-first assertion on the expected state |
| Selecting `.text-amber-400` or other utility classes | `data-testid` or role-based locators |
| Logging in inside every test | Global setup + `storageState` reuse |
| Testing every CRUD path in E2E | Keep E2E to the five critical journeys; push the rest down the pyramid |
| Screenshots that flake | Fixed viewport, `animations: 'disabled'`, mask dynamic regions (posters, timestamps) |
| Running against a deployed environment | Target local Kestrel via the `webServer` option for isolation and speed |
