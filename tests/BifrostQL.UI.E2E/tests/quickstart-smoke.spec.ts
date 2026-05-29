import { test, expect } from '@playwright/test';
import { runQuickstart, openTable, dataRows } from './helpers';

/**
 * Per-schema quickstart smoke: every example database creates, binds, and
 * renders its full table list with browseable data. Also guards the
 * duplicate-PK join-alias rendering regression. Implicitly covers the
 * dbJoinSchema.fieldName schema fix — without it the editor fails to render
 * at all (introspection error), so every assertion here would fail.
 */

const SCHEMAS: { displayName: string; tables: string[] }[] = [
  { displayName: 'Blog', tables: ['authors', 'categories', 'posts', 'tags', 'post_tags', 'comments'] },
  { displayName: 'E-Commerce', tables: ['categories', 'customers', 'addresses', 'products', 'orders', 'order_items', 'reviews'] },
  { displayName: 'CRM', tables: ['deal_stages', 'companies', 'contacts', 'deals', 'activities', 'notes'] },
  { displayName: 'Classroom', tables: ['instructors', 'courses', 'students', 'enrollments', 'assignments', 'submissions'] },
  { displayName: 'Project Tracker', tables: ['workspaces', 'members', 'projects', 'sections', 'tasks', 'labels', 'task_labels', 'task_assignments'] },
];

// Tables whose FK label column equals the PK — these regressed when a
// DistinctBy(c => c.DbName) dropped the duplicate alias.
const JOIN_REGRESSION = [
  { schema: 'Classroom', table: 'assignments' },
  { schema: 'Classroom', table: 'submissions' },
  { schema: 'Classroom', table: 'enrollments' },
  { schema: 'Project Tracker', table: 'labels' },
  { schema: 'Project Tracker', table: 'task_labels' },
];

for (const schema of SCHEMAS) {
  test.describe(`${schema.displayName} quickstart`, () => {
    test('creates the database and lists every table', async ({ page }) => {
      await runQuickstart(page, schema.displayName);
      for (const table of schema.tables) {
        await expect(
          page.locator(`a.plain-link[href="/${table}"]`),
          `sidebar should link to '${table}'`
        ).toHaveCount(1);
      }
    });

    for (const table of schema.tables) {
      test(`loads data rows for ${table}`, async ({ page }) => {
        await runQuickstart(page, schema.displayName);
        await openTable(page, table);
        await expect(dataRows(page).first()).toBeVisible({ timeout: 10_000 });
        expect(await dataRows(page).count()).toBeGreaterThan(0);
      });
    }
  });
}

test.describe('Join column rendering (duplicate PK alias)', () => {
  for (const { schema, table } of JOIN_REGRESSION) {
    test(`${schema} → ${table} renders without a GraphQL error`, async ({ page }) => {
      await runQuickstart(page, schema);
      await openTable(page, table);
      await expect(dataRows(page).first()).toBeVisible({ timeout: 10_000 });
      // No "Unable to find queryField" / introspection error surfaced.
      await expect(page.getByText(/Unable to find queryField|Cannot query field/i)).toHaveCount(0);
    });
  }
});
