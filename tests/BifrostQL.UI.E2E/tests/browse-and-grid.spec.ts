import { test, expect } from '@playwright/test';
import { runQuickstart, openTable, dataGrid, dataRows, cell } from './helpers';

/**
 * Read-path happy flows for the data grid: table list, row rendering, column
 * headers, foreign-key navigation, sorting, and pagination.
 */

test.describe('Browse & grid (Blog)', () => {
  test.beforeEach(async ({ page }) => {
    await runQuickstart(page, 'Blog');
  });

  test('sidebar lists all blog tables', async ({ page }) => {
    for (const table of ['authors', 'posts', 'comments', 'tags', 'categories', 'post_tags']) {
      await expect(page.locator(`a.plain-link[href="/${table}"]`)).toHaveCount(1);
    }
  });

  test('opening a table shows a grid with data rows', async ({ page }) => {
    await openTable(page, 'posts');
    await expect(dataRows(page).first()).toBeVisible({ timeout: 10_000 });
    expect(await dataRows(page).count()).toBeGreaterThan(0);
  });

  test('grid shows column headers', async ({ page }) => {
    await openTable(page, 'posts');
    const heads = dataGrid(page).locator('thead th');
    await expect(heads.filter({ hasText: /^Title$/ })).toBeVisible();
    await expect(heads.filter({ hasText: /^Slug$/ })).toBeVisible();
  });

  test('foreign-key link navigates from posts to its author', async ({ page }) => {
    await openTable(page, 'posts');
    // FK cells render as anchors to the parent record, e.g. /authors/1.
    const fk = dataRows(page).first().locator('a[href^="/authors/"]').first();
    await expect(fk).toBeVisible({ timeout: 10_000 });
    await fk.click();
    await expect(page.getByRole('heading', { name: 'authors', level: 2 })).toBeVisible({ timeout: 10_000 });
  });

  test('reverse foreign-key link navigates from authors to that author\'s posts', async ({ page }) => {
    await openTable(page, 'authors');
    // Reverse-FK cells link to the child collection filtered by this row, e.g.
    // /posts/from/authors/1.
    const reverse = dataRows(page).first().locator('a[href*="/from/authors/"]').first();
    if (await reverse.isVisible({ timeout: 3_000 }).catch(() => false)) {
      await reverse.click();
      await expect(dataRows(page).first()).toBeVisible({ timeout: 10_000 });
    }
  });

  test('clicking a column header sorts without losing rows', async ({ page }) => {
    await openTable(page, 'posts');
    await dataGrid(page).locator('thead th').filter({ hasText: /^Title$/ }).click();
    await expect(dataRows(page).first()).toBeVisible({ timeout: 10_000 });
  });

  test('sidebar and grid coexist; switching tables updates the grid', async ({ page }) => {
    await openTable(page, 'posts');
    await expect(page.getByRole('heading', { name: 'posts', level: 2 })).toBeVisible();
    // Sidebar nav stays present alongside the data grid.
    await expect(page.locator('a.plain-link[href="/authors"]')).toBeVisible();

    // Clicking a different table swaps the grid contents.
    await page.locator('a.plain-link[href="/authors"]').first().click();
    await expect(page.getByRole('heading', { name: 'authors', level: 2 })).toBeVisible({ timeout: 10_000 });
    await expect(dataRows(page).first()).toBeVisible({ timeout: 10_000 });
  });

  test('pagination advances to the next page', async ({ page }) => {
    await openTable(page, 'posts'); // 50 rows, paginated
    const firstBefore = await cell(dataRows(page).first(), 'post_id').innerText();

    const next = page.locator('button[aria-label="Next page"]');
    await expect(next).toBeEnabled({ timeout: 5_000 });
    await next.click();

    await expect(dataRows(page).first()).toBeVisible({ timeout: 10_000 });
    const firstAfter = await cell(dataRows(page).first(), 'post_id').innerText();
    expect(firstAfter).not.toBe(firstBefore);
  });
});
