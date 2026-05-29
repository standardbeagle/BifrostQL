import { test, expect, Page } from '@playwright/test';
import { runQuickstart, openTable, dataRows, cell, openEditRow, openAddRow, deleteRow } from './helpers';

/**
 * Core data round-trips through the edit-db editor: edit+save+persist, insert,
 * and delete. These are the central promises of "edit-db" and were previously
 * untested (the old suite only opened the edit dialog, never saved).
 *
 * Persistence is verified two ways:
 *  - edit: the changed value is reflected in the grid AND survives a reload.
 *  - insert/delete: the sidebar per-table row count (the `title="N rows"`
 *    badge) moves by ±1, which is robust against grid pagination.
 */

/** The sidebar row-count badge for a table (independent of grid paging). */
async function sidebarRowCount(page: Page, table: string): Promise<number> {
  const bar = page.locator(`a.plain-link[href="/${table}"] span[title$="rows"]`).first();
  const title = await bar.getAttribute('title');
  const n = parseInt(title ?? '', 10);
  expect(Number.isFinite(n), `row-count badge for ${table} ("${title}")`).toBe(true);
  return n;
}

test.describe('CRUD round-trips (Blog)', () => {
  test.beforeEach(async ({ page }) => {
    await runQuickstart(page, 'Blog');
  });

  test('edit a row, save, and the change persists across reload', async ({ page }) => {
    await openTable(page, 'posts');

    const dialog = await openEditRow(page, 0);
    await expect(dialog.locator('[data-slot=dialog-title]')).toContainText('Posts');

    const titleField = dialog.locator('#title');
    await expect(titleField).toBeVisible();
    const newTitle = `Edited Title ${Date.now()}`;
    await titleField.fill(newTitle);

    await dialog.getByRole('button', { name: 'Save' }).click();
    await expect(dialog).not.toBeVisible({ timeout: 10_000 });

    // Reflected in the grid (row 0 is still post_id 1 under default PK order).
    await expect(cell(dataRows(page).first(), 'title')).toContainText(newTitle, { timeout: 10_000 });

    // Survives a full reload (real DB write, not just client state).
    await page.reload();
    await openTable(page, 'posts');
    await expect(cell(dataRows(page).first(), 'title')).toContainText(newTitle, { timeout: 10_000 });
  });

  test('insert a new row increments the table row count', async ({ page }) => {
    // tags has no required FK columns, so a text-only insert succeeds.
    await openTable(page, 'tags');
    const before = await sidebarRowCount(page, 'tags');

    const dialog = await openAddRow(page);
    await expect(dialog.locator('[data-slot=dialog-title]')).toContainText('Tags');

    const stamp = Date.now();
    // Fill every text field; required ones (*) are covered, optionals are harmless.
    const textareas = dialog.locator('textarea');
    const count = await textareas.count();
    for (let i = 0; i < count; i++) {
      await textareas.nth(i).fill(`e2e_${stamp}_${i}`);
    }

    await dialog.getByRole('button', { name: 'Create' }).click();
    await expect(dialog).not.toBeVisible({ timeout: 10_000 });

    // After Create the editor leaves the list view; reload to read the
    // persisted row count from the freshly bound schema.
    await page.reload();
    await openTable(page, 'tags');
    await expect
      .poll(() => sidebarRowCount(page, 'tags'), { timeout: 10_000 })
      .toBe(before + 1);
  });

  test('delete a row decrements the table row count', async ({ page }) => {
    // comments is a leaf table (nothing references it) so a delete won't trip
    // a foreign-key constraint.
    await openTable(page, 'comments');
    const before = await sidebarRowCount(page, 'comments');

    await deleteRow(page, 0);

    // Reload so the row-count badge reflects the committed delete.
    await page.reload();
    await openTable(page, 'comments');
    await expect
      .poll(() => sidebarRowCount(page, 'comments'), { timeout: 10_000 })
      .toBe(before - 1);
  });
});
