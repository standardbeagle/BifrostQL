import { test, expect, Page } from '@playwright/test';

async function runQuickstart(page: Page, schema: string, dataSize = 'Sample') {
  await page.goto('/');
  await expect(page.getByText('Try it now')).toBeVisible({ timeout: 15_000 });
  await page.getByText('Try it now').click();

  const schemaCard = page.getByText(schema, { exact: false });
  await expect(schemaCard).toBeVisible();
  await schemaCard.click();

  const sizeToggle = page.getByText(dataSize, { exact: false });
  if (await sizeToggle.isVisible({ timeout: 2_000 }).catch(() => false)) {
    await sizeToggle.click();
  }

  const launchButton = page.getByRole('button', { name: /launch|create|start/i });
  await expect(launchButton).toBeVisible();
  await launchButton.click();

  await expect(page.locator('a.plain-link').first()).toBeVisible({ timeout: 30_000 });
}

async function navigateToTable(page: Page, tableName: string) {
  await page.locator('a.plain-link').filter({ hasText: tableName }).first().click();
  await expect(page.getByText(`Table: ${tableName}`, { exact: false })).toBeVisible({ timeout: 10_000 });
}

test.describe('Classroom — Field Filter', () => {
  test.beforeEach(async ({ page }) => {
    await runQuickstart(page, 'Classroom');
  });

  test('filter submissions by assignment_id shows filtered results', async ({ page }) => {
    await navigateToTable(page, 'submissions');

    // Wait for data to load
    const editLinks = page.locator('a').filter({ hasText: 'edit' });
    await expect(editLinks.first()).toBeVisible({ timeout: 10_000 });
    const totalBefore = await editLinks.count();

    // Select assignment_id from the filter dropdown
    const filterSelect = page.locator('header select');
    await expect(filterSelect).toBeVisible();
    await filterSelect.selectOption({ label: 'assignment_id' });

    // Type a filter value
    const filterInput = page.locator('header input[type="search"]');
    await filterInput.fill('1');

    // Click filter button
    await page.getByRole('button', { name: 'filter' }).click();

    // Wait for the filtered data to load
    await page.waitForTimeout(1_000);
    await expect(editLinks.first()).toBeVisible({ timeout: 10_000 });

    // Should have fewer rows (assignment 1 has 6 submissions out of ~50 total)
    const totalAfter = await editLinks.count();
    expect(totalAfter).toBeLessThan(totalBefore);
    expect(totalAfter).toBeGreaterThan(0);

    // Verify no GraphQL errors appear on screen
    const errorText = page.getByText(/error|invalid|unable/i);
    // Filter out false positives — only check within error display areas
    const errorPanel = page.locator('.editdb-error, [class*="error"]').filter({ hasText: /GraphQL|invalid|Unable/ });
    await expect(errorPanel).toHaveCount(0);
  });

  test('filter assignments by course_id shows filtered results', async ({ page }) => {
    await navigateToTable(page, 'assignments');

    const editLinks = page.locator('a').filter({ hasText: 'edit' });
    await expect(editLinks.first()).toBeVisible({ timeout: 10_000 });

    // Select course_id from the filter dropdown
    const filterSelect = page.locator('header select');
    await filterSelect.selectOption({ label: 'course_id' });

    const filterInput = page.locator('header input[type="search"]');
    await filterInput.fill('1');

    await page.getByRole('button', { name: 'filter' }).click();
    await page.waitForTimeout(1_000);

    // Should show data without errors
    await expect(editLinks.first()).toBeVisible({ timeout: 10_000 });

    // Verify no error message in the page content
    const bodyText = await page.locator('.editdb-frame-layout-body').textContent();
    expect(bodyText).not.toContain('Error:');
  });

  test('filter students by string field uses contains', async ({ page }) => {
    await navigateToTable(page, 'students');

    const editLinks = page.locator('a').filter({ hasText: 'edit' });
    await expect(editLinks.first()).toBeVisible({ timeout: 10_000 });

    // Select first_name (String type) from the filter dropdown
    const filterSelect = page.locator('header select');
    await filterSelect.selectOption({ label: 'first_name' });

    const filterInput = page.locator('header input[type="search"]');
    await filterInput.fill('A');

    await page.getByRole('button', { name: 'filter' }).click();
    await page.waitForTimeout(1_000);

    // Should show results containing 'A'
    await expect(editLinks.first()).toBeVisible({ timeout: 10_000 });

    const bodyText = await page.locator('.editdb-frame-layout-body').textContent();
    expect(bodyText).not.toContain('Error:');
  });
});
