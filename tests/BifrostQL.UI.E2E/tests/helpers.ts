import { Page, Locator, expect } from '@playwright/test';

/**
 * Shared flow helpers for the edit-db editor UI.
 *
 * The editor was rewritten on a shadcn/Radix/Tailwind stack (commit 86fc0ef).
 * Key DOM facts these helpers encapsulate so specs don't hard-code them:
 *  - The sidebar table list and the data grid are BOTH shadcn <table>s. The
 *    data grid is distinguished by data-grid cells carrying `data-col-id`.
 *  - Sidebar table links are `a.plain-link[href="/<table>"]`.
 *  - The selected-table heading is an <h2> with the raw table name.
 *  - Per-row Edit/Delete are floating buttons revealed on row hover, rendered
 *    OUTSIDE the <tr> — so they are located page-wide after hovering the row.
 *  - The create/edit form is a Radix dialog ([role=dialog][data-slot=dialog-content]).
 *    Text fields are <textarea id="<column>">; FK fields are Radix comboboxes.
 *    Submit is "Create" for insert, "Save" for edit.
 *  - Delete pops a confirm dialog with a "Delete row" button.
 */

export async function runQuickstart(page: Page, schemaDisplayName: string, dataSize = 'Sample'): Promise<void> {
  await page.goto('/');
  await expect(page.getByText('Try it now')).toBeVisible({ timeout: 15_000 });
  await page.getByText('Try it now').click();

  const card = page.getByText(schemaDisplayName, { exact: false }).first();
  await expect(card).toBeVisible();
  await card.click();

  const size = page.getByText(dataSize, { exact: false });
  if (await size.isVisible({ timeout: 2_000 }).catch(() => false)) {
    await size.click();
  }

  await page.getByRole('button', { name: /launch|create|start/i }).first().click();

  // Editor renders its sidebar table list once the schema is bound.
  await expect(page.locator('a.plain-link').first()).toBeVisible({ timeout: 30_000 });
}

/** The data grid table — disambiguated from the sidebar table by data-col-id cells. */
export function dataGrid(page: Page): Locator {
  return page.locator('table:has(td[data-col-id])');
}

/** Data rows of the grid (excludes the header row). */
export function dataRows(page: Page): Locator {
  return dataGrid(page).locator('tbody tr');
}

/** A specific column cell within a data row. */
export function cell(row: Locator, colId: string): Locator {
  return row.locator(`td[data-col-id="${colId}"]`);
}

export async function openTable(page: Page, table: string): Promise<void> {
  await page.locator(`a.plain-link[href="/${table}"]`).first().click();
  await expect(page.getByRole('heading', { name: table, level: 2 })).toBeVisible({ timeout: 10_000 });
  await expect(dataGrid(page)).toBeVisible({ timeout: 10_000 });
}

/**
 * Hover a row and open its Edit dialog. Returns the dialog locator.
 *
 * Contract: the Edit/Delete action buttons render OUTSIDE the <tr> and are
 * located page-wide with .first() after hover, so this reliably targets the
 * top acted-on row only (rowIndex 0 in practice). Acting on an arbitrary row
 * would require scoping the revealed buttons to that specific hovered row.
 */
export async function openEditRow(page: Page, rowIndex = 0): Promise<Locator> {
  await dataRows(page).nth(rowIndex).hover();
  const editBtn = page.locator('button[aria-label="Edit row"]').first();
  await expect(editBtn).toBeVisible({ timeout: 5_000 });
  await editBtn.click();
  const dialog = page.locator('[role=dialog][data-slot=dialog-content]').first();
  await expect(dialog).toBeVisible({ timeout: 10_000 });
  return dialog;
}

/** Open the Add (create) dialog. Returns the dialog locator. */
export async function openAddRow(page: Page): Promise<Locator> {
  await page.getByRole('button', { name: /^add$/i }).first().click();
  const dialog = page.locator('[role=dialog][data-slot=dialog-content]').first();
  await expect(dialog).toBeVisible({ timeout: 10_000 });
  return dialog;
}

/** Delete the row at rowIndex via the hover action + confirm dialog.
 *  Same row-0 contract as openEditRow (page-wide .first() after hover). */
export async function deleteRow(page: Page, rowIndex = 0): Promise<void> {
  await dataRows(page).nth(rowIndex).hover();
  const delBtn = page.locator('button[aria-label="Delete row"]').first();
  await expect(delBtn).toBeVisible({ timeout: 5_000 });
  await delBtn.click();
  const confirm = page.locator('[role=dialog]').filter({ hasText: /Delete \d+ row/ });
  await expect(confirm).toBeVisible({ timeout: 5_000 });
  await confirm.getByRole('button', { name: 'Delete row' }).click();
  await expect(confirm).not.toBeVisible({ timeout: 10_000 });
}

/** Pick an option from a Radix select trigger by visible-text pattern. */
export async function selectOption(page: Page, trigger: Locator, optionPattern: RegExp): Promise<void> {
  await trigger.click();
  await page.getByRole('option', { name: optionPattern }).first().click();
}
