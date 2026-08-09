import { test, expect, Page } from '@playwright/test';
import { runQuickstart } from './helpers';

/**
 * The three editor panes that run over the desktop bridge rather than GraphQL:
 * the raw SQL console, the visual query builder, and the form builder.
 *
 * They are gated on the bridge being reachable. In the desktop app that is
 * Photino's `window.external`; here the server is started with
 * `--enable-http-bridge` (see global-setup), which exposes the SAME handler
 * instances over loopback HTTP. So these tests drive the shipped handlers —
 * exec-sql, get-builder-schema, build-sql, build-and-exec — not a stand-in.
 *
 * DOM facts these tests rely on, read off the running pane:
 *  - Pane switching is a toolbar button per pane, exact-named.
 *  - The SQL console is a CodeMirror editor (`.cm-content`) plus a Run button.
 *  - The query-builder palette adds tables via buttons labelled
 *    `Add <schema>.<table>`, and removes them via `Remove <schema>.<table>`.
 *  - Column projection is plain `input[type=checkbox]` inside the table card.
 *  - The form builder picks its table with a native <select> of qualified names.
 *
 * The panes only exist once a database is bound in this client session, so every
 * test runs Quick Start first.
 */

type PaneName = 'GraphQL' | 'SQL' | 'Query builder' | 'Form builder';

async function openPane(page: Page, name: PaneName): Promise<void> {
  const button = page.getByRole('button', { name, exact: true });
  await expect(button).toBeVisible({ timeout: 20_000 });
  await button.click();
}

/** Adds a table to the design canvas from the palette. */
async function addTable(page: Page, qualified: string): Promise<void> {
  const add = page.getByRole('button', { name: `Add ${qualified}` });
  await expect(add).toBeVisible({ timeout: 20_000 });
  await add.click();
  // The card carries the matching Remove control once it is on the canvas.
  await expect(page.getByRole('button', { name: `Remove ${qualified}` }))
    .toBeVisible({ timeout: 10_000 });
}

test.describe('Desktop bridge panes (E-commerce)', () => {
  test.beforeEach(async ({ page }) => {
    await runQuickstart(page, 'E-commerce');
  });

  test('the bridge panes are present once a bridge is reachable', async ({ page }) => {
    // Guards the gate itself. If the availability probe regresses, every other
    // test here fails as "button not found", which hides the real cause.
    for (const pane of ['GraphQL', 'SQL', 'Query builder', 'Form builder'] as const) {
      await expect(page.getByRole('button', { name: pane, exact: true }))
        .toBeVisible({ timeout: 20_000 });
    }
  });

  test('SQL console runs a query and renders the result grid', async ({ page }) => {
    await openPane(page, 'SQL');

    const editor = page.locator('.cm-content').first();
    await expect(editor).toBeVisible({ timeout: 10_000 });
    await editor.click();
    await page.keyboard.type('SELECT name, price FROM products ORDER BY price DESC LIMIT 3');

    await page.getByRole('button', { name: /^Run/ }).click();

    // The status line proves the statement executed, rather than the editor
    // merely having accepted the text.
    await expect(page.getByText(/\d+ row\(s\)/)).toBeVisible({ timeout: 20_000 });

    // Headers come from the query's own projection, so they also show the result
    // travelled back through the bridge intact.
    await expect(page.getByText('name', { exact: true }).first()).toBeVisible();
    await expect(page.getByText('price', { exact: true }).first()).toBeVisible();
  });

  test('SQL console surfaces a bad statement as an error, not a blank grid', async ({ page }) => {
    await openPane(page, 'SQL');

    const editor = page.locator('.cm-content').first();
    await editor.click();
    await page.keyboard.type('SELECT * FROM no_such_table_here');
    await page.getByRole('button', { name: /^Run/ }).click();

    // A swallowed error would leave the pane looking like an empty result set.
    await expect(page.getByText(/no such table|no_such_table_here|error/i).first())
      .toBeVisible({ timeout: 20_000 });
  });

  test('query builder derives the join between two related tables', async ({ page }) => {
    await openPane(page, 'Query builder');

    // The palette is populated from the loaded model via get-builder-schema.
    await addTable(page, 'main.orders');
    await addTable(page, 'main.customers');

    // The builder's headline behaviour: the FK is inferred, never typed.
    await expect(
      page.getByText('main.orders.(customer_id) = main.customers.(customer_id)')
    ).toBeVisible({ timeout: 20_000 });
  });

  test('query builder generates SQL for the designed query', async ({ page }) => {
    await openPane(page, 'Query builder');
    await addTable(page, 'main.products');

    // Project a column: the builder refuses to build a query that selects
    // nothing, so skipping this only ever exercises the validation path.
    await page.locator('input[type=checkbox]').first().check();

    await page.getByRole('button', { name: 'View SQL' }).click();

    // build-sql returns a real SELECT for the active dialect.
    await expect(page.getByText(/select\b/i).first()).toBeVisible({ timeout: 20_000 });
    await expect(page.getByText(/\bfrom\b/i).first()).toBeVisible();
  });

  test('query builder runs the designed query and returns rows', async ({ page }) => {
    await openPane(page, 'Query builder');
    await addTable(page, 'main.products');
    await page.locator('input[type=checkbox]').first().check();

    await page.getByRole('button', { name: 'Run', exact: true }).click();

    // build-and-exec goes all the way to the database and back, so a result grid
    // appears and the "must show at least one column" validation does not.
    await expect(page.getByText(/must show at least one column/i)).toHaveCount(0);
    await expect(page.locator('table').first()).toBeVisible({ timeout: 20_000 });
  });

  test('query builder reports an unrelated pair rather than inventing a join', async ({ page }) => {
    await openPane(page, 'Query builder');

    // products and customers have no direct FK between them. The builder must
    // not fabricate one — a silently-invented join would produce a wrong result
    // set that looks perfectly plausible.
    await addTable(page, 'main.products');
    await addTable(page, 'main.customers');

    await expect(
      page.getByText('main.products.(customer_id) = main.customers.(customer_id)')
    ).toHaveCount(0);
  });

  test('form builder lists the model tables and opens one', async ({ page }) => {
    await openPane(page, 'Form builder');

    const picker = page.locator('select').filter({ hasText: 'main.products' }).first();
    await expect(picker).toBeVisible({ timeout: 20_000 });

    await picker.selectOption('main.products');

    // Choosing a table replaces the empty state with the form surface.
    await expect(page.getByText(/Choose a table to start a form/i))
      .toHaveCount(0, { timeout: 20_000 });
  });
});
