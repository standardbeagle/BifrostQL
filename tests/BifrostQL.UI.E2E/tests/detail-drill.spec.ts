import { test, expect } from '@playwright/test';
import { runQuickstart, openTable, dataGrid, dataRows } from './helpers';

/**
 * Nested master-detail drill: selecting a row in a table that has child tables
 * opens those children in a detail panel beneath it, and selecting a child row
 * opens ITS children one level deeper. Regression guard for the bug where the
 * detail panel was one level only — selecting a post under an author showed
 * nothing beneath (fixed by making edit-db's DetailPanel recurse).
 */
test.describe('Nested master-detail drill (Blog)', () => {
  test.beforeEach(async ({ page }) => {
    await runQuickstart(page, 'Blog');
  });

  test('author → posts → comments each open beneath the previous', async ({ page }) => {
    await openTable(page, 'authors');
    const grids = page.locator('table:has(td[data-col-id])');

    // Select an author row (click its primary-key cell, not a link/button).
    await dataRows(page).first().locator('td[data-col-id]').nth(1).click();

    // Posts (a child of authors) open in a detail panel beneath the grid.
    await expect(grids).toHaveCount(2, { timeout: 10_000 });
    await expect(page.getByText('Detail:')).toHaveCount(1);

    // Select a post in that detail panel — its children (comments) must open
    // in a further detail panel beneath, i.e. the next level down.
    await grids.nth(1).locator('tbody tr').first().locator('td[data-col-id]').nth(1).click();

    await expect(grids).toHaveCount(3, { timeout: 10_000 });
    await expect(page.getByText('Detail:')).toHaveCount(2);
  });
});
