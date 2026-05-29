import { test, expect, Page } from '@playwright/test';
import { runQuickstart, openTable, dataRows, selectOption } from './helpers';

/**
 * Field-filter happy paths. The toolbar filter is a Radix combobox (pick the
 * column) + a search input (the value) + a Filter button. Verifies numeric
 * equality narrowing and string "contains" matching, with no GraphQL error.
 */

async function applyFilter(page: Page, columnPattern: RegExp, value: string): Promise<void> {
  // First combobox in the table-view header is the filter-column selector.
  const columnSelect = page.locator('header [data-slot="select-trigger"]').first();
  await selectOption(page, columnSelect, columnPattern);
  await page.locator('header input[type="search"]').fill(value);
  await page.getByRole('button', { name: /^filter$/i }).click();
  await expect(dataRows(page).first()).toBeVisible({ timeout: 10_000 });
}

async function expectNoError(page: Page): Promise<void> {
  await expect(page.getByText(/Cannot query field|Unable to find|GraphQL error/i)).toHaveCount(0);
}

test.describe('Classroom field filter', () => {
  test.beforeEach(async ({ page }) => {
    await runQuickstart(page, 'Classroom');
  });

  test('filtering submissions by assignment narrows the result set', async ({ page }) => {
    await openTable(page, 'submissions');
    const before = await dataRows(page).count();

    await applyFilter(page, /assignment/i, '1');

    const after = await dataRows(page).count();
    expect(after).toBeGreaterThan(0);
    // assignment 1 has ~6 submissions of ~50, so the visible page genuinely
    // shrinks — a strict < proves the filter narrowed (>= would pass vacuously).
    expect(after).toBeLessThan(before);
    await expectNoError(page);
  });

  test('filtering assignments by course returns rows without error', async ({ page }) => {
    await openTable(page, 'assignments');
    await applyFilter(page, /course/i, '1');
    expect(await dataRows(page).count()).toBeGreaterThan(0);
    await expectNoError(page);
  });

  test('filtering students by first name uses contains matching', async ({ page }) => {
    await openTable(page, 'students');
    await applyFilter(page, /first.?name/i, 'a');
    expect(await dataRows(page).count()).toBeGreaterThan(0);
    await expectNoError(page);
  });
});
