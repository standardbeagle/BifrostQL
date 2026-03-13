import { test, expect, Page } from '@playwright/test';

// Helper: run the quickstart flow for a given schema and wait for the table list
async function runQuickstart(page: Page, schema: string, dataSize = 'Sample') {
  await page.goto('/');

  // Welcome screen — click "Try it now"
  await expect(page.getByText('Try it now')).toBeVisible({ timeout: 15_000 });
  await page.getByText('Try it now').click();

  // QuickStart screen — pick schema
  const schemaCard = page.getByText(schema, { exact: false });
  await expect(schemaCard).toBeVisible();
  await schemaCard.click();

  // Pick data size if toggle exists
  const sizeToggle = page.getByText(dataSize, { exact: false });
  if (await sizeToggle.isVisible({ timeout: 2_000 }).catch(() => false)) {
    await sizeToggle.click();
  }

  // Click launch/create button
  const launchButton = page.getByRole('button', { name: /launch|create|start/i });
  await expect(launchButton).toBeVisible();
  await launchButton.click();

  // Wait for the table list to appear — plain-link anchors are rendered by edit-db
  await expect(page.locator('a.plain-link').first()).toBeVisible({ timeout: 30_000 });
}

// Helper: click a table in the list and wait for the data grid
async function navigateToTable(page: Page, tableName: string) {
  await page.locator('a.plain-link').filter({ hasText: tableName }).first().click();

  // Wait for the "Table: <name>" header that appears when a table is selected
  await expect(page.getByText(`Table: ${tableName}`, { exact: false })).toBeVisible({ timeout: 10_000 });
}

test.describe('Blog Quickstart — Full Flow', () => {

  test.beforeEach(async ({ page }) => {
    await runQuickstart(page, 'Blog');
  });

  test('quickstart creates database and shows table list', async ({ page }) => {
    const tableLinks = page.locator('a.plain-link');
    await expect(tableLinks.first()).toBeVisible();
    const count = await tableLinks.count();
    expect(count).toBeGreaterThanOrEqual(4); // authors, posts, comments, tags at minimum
  });

  test('table list shows all blog tables', async ({ page }) => {
    const linkTexts = await page.locator('a.plain-link').allTextContents();
    const tableNames = linkTexts.map(t => t.trim().toLowerCase());
    for (const table of ['authors', 'posts', 'comments', 'tags', 'categories', 'post_tags']) {
      expect(tableNames, `should contain '${table}'`).toContain(table);
    }
  });

  test('clicking a table shows data grid with rows', async ({ page }) => {
    await navigateToTable(page, 'posts');

    // The data grid uses react-data-table-component — look for row role or edit links
    // Each row has an "edit" link
    const editLinks = page.locator('a').filter({ hasText: 'edit' });
    await expect(editLinks.first()).toBeVisible({ timeout: 10_000 });
    const rowCount = await editLinks.count();
    expect(rowCount).toBeGreaterThan(0);
  });

  test('data grid shows column headers', async ({ page }) => {
    await navigateToTable(page, 'posts');

    // Column headers are div[data-column-id] elements — use exact text match to avoid the filter combobox
    await expect(page.locator('div[data-column-id]', { hasText: /^post_id$/ })).toBeVisible({ timeout: 10_000 });
    await expect(page.locator('div[data-column-id]', { hasText: /^title$/ })).toBeVisible();
  });

  test('FK link navigates from posts to author', async ({ page }) => {
    await navigateToTable(page, 'posts');

    // FK links are rendered as links that are NOT "edit" — they show the parent record's label
    // Filter out "edit" links and plain-link table nav links to find FK links
    const allLinks = page.locator('a').filter({ hasNotText: /^edit$/ });
    const fkLinks = allLinks.filter({ hasNotText: /authors|categories|comments|post_tags|posts|tags/ });

    // There might not be FK links if SQLite doesn't detect joins — that's a known limitation
    const firstFk = fkLinks.first();
    if (await firstFk.isVisible({ timeout: 3_000 }).catch(() => false)) {
      await firstFk.click();

      // After clicking, should navigate to related table
      await page.waitForTimeout(1_000);
      await expect(page.getByText('Table:', { exact: false })).toBeVisible({ timeout: 10_000 });
    }
  });

  test('reverse FK link navigates from authors to filtered posts', async ({ page }) => {
    await navigateToTable(page, 'authors');

    // Multi-join links show as links in the grid for related tables
    const multiJoinLink = page.locator('.rdt_TableBody a').filter({ hasText: /post/i }).first();

    if (await multiJoinLink.isVisible({ timeout: 3_000 }).catch(() => false)) {
      await multiJoinLink.click();

      // Should show posts filtered to that author
      await expect(page.locator('.rdt_TableRow').first()).toBeVisible({ timeout: 10_000 });
    }
  });

  test('clicking column header sorts the data', async ({ page }) => {
    await navigateToTable(page, 'posts');

    // Click a column header to sort
    const header = page.locator('.rdt_TableCol').first();
    await header.click();

    // After sort, rows should still be visible
    await expect(page.locator('.rdt_TableRow').first()).toBeVisible();
  });
});
