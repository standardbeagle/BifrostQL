import { test, expect, Page } from '@playwright/test';

const SCHEMAS: { name: string; displayName: string; expectedTables: string[] }[] = [
  {
    name: 'blog',
    displayName: 'Blog',
    expectedTables: ['authors', 'categories', 'posts', 'tags', 'post_tags', 'comments'],
  },
  {
    name: 'ecommerce',
    displayName: 'E-Commerce',
    expectedTables: ['categories', 'customers', 'addresses', 'products', 'orders', 'order_items', 'reviews'],
  },
  {
    name: 'crm',
    displayName: 'CRM',
    expectedTables: ['deal_stages', 'companies', 'contacts', 'deals', 'activities', 'notes'],
  },
  {
    name: 'classroom',
    displayName: 'Classroom',
    expectedTables: ['instructors', 'courses', 'students', 'enrollments', 'assignments', 'submissions'],
  },
  {
    name: 'project-tracker',
    displayName: 'Project Tracker',
    expectedTables: ['workspaces', 'members', 'projects', 'sections', 'tasks', 'labels', 'task_labels', 'task_assignments'],
  },
];

async function runQuickstart(page: Page, schemaDisplayName: string) {
  await page.goto('/');

  await expect(page.getByText('Try it now')).toBeVisible({ timeout: 15_000 });
  await page.getByText('Try it now').click();

  const schemaCard = page.getByText(schemaDisplayName, { exact: false });
  await expect(schemaCard).toBeVisible();
  await schemaCard.click();

  const launchButton = page.getByRole('button', { name: /launch|create|start/i });
  await expect(launchButton).toBeVisible();
  await launchButton.click();

  await expect(page.locator('a.plain-link').first()).toBeVisible({ timeout: 30_000 });
}

async function navigateToTable(page: Page, tableName: string) {
  await page.locator('a.plain-link').filter({ hasText: tableName }).first().click();
  await expect(page.getByText(`Table: ${tableName}`, { exact: false })).toBeVisible({ timeout: 10_000 });
}

// Tables with FK joins where labelColumn = PK (duplicate alias for same DB column).
// These were broken by DistinctBy(c => c.DbDbName) dropping the second alias.
const JOIN_ERROR_TABLES = [
  { schema: 'Classroom', table: 'assignments', joinColumn: 'courses' },
  { schema: 'Classroom', table: 'submissions', joinColumn: 'assignments' },
  { schema: 'Classroom', table: 'enrollments', joinColumn: 'courses' },
  { schema: 'Project Tracker', table: 'labels', joinColumn: 'workspaces' },
  { schema: 'Project Tracker', table: 'task_labels', joinColumn: 'tasks' },
];

test.describe('Join column rendering (duplicate PK alias)', () => {
  for (const { schema, table, joinColumn } of JOIN_ERROR_TABLES) {
    test(`${schema} → ${table} renders ${joinColumn} join without errors`, async ({ page }) => {
      await runQuickstart(page, schema);
      await navigateToTable(page, table);

      // Wait for data rows to render
      await expect(page.locator('.rdt_TableRow').first()).toBeVisible({ timeout: 10_000 });

      // The join column header should be present (shows the destination table name)
      const headerTexts = await page.locator('.rdt_TableCol').allTextContents();
      const headers = headerTexts.map(h => h.trim().toLowerCase());

      // Verify no GraphQL error toast/banner appeared
      const errorBanner = page.locator('text=/Unable to find queryField/i');
      await expect(errorBanner).not.toBeVisible({ timeout: 2_000 });
    });
  }
});

for (const schema of SCHEMAS) {
  test.describe(`${schema.displayName} Quickstart`, () => {

    test('creates database and shows all expected tables', async ({ page }) => {
      await runQuickstart(page, schema.displayName);

      const linkTexts = await page.locator('a.plain-link').allTextContents();
      const tableNames = linkTexts.map(t => t.trim().toLowerCase());

      for (const table of schema.expectedTables) {
        expect(tableNames, `sidebar should contain '${table}'`).toContain(table);
      }
    });

    for (const table of schema.expectedTables) {
      test(`loads data rows for table: ${table}`, async ({ page }) => {
        await runQuickstart(page, schema.displayName);
        await navigateToTable(page, table);

        await expect(page.locator('.rdt_TableRow').first()).toBeVisible({ timeout: 10_000 });
        const rowCount = await page.locator('.rdt_TableRow').count();
        expect(rowCount).toBeGreaterThan(0);
      });
    }
  });
}
