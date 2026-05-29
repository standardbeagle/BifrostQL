import { defineConfig } from '@playwright/test';

// Iteration-only config: assumes a BifrostQL.UI headless server is ALREADY
// running at DEV_BASE_URL (default http://localhost:5599). No globalSetup, so
// specs run instantly against the live server while developing selectors.
// The committed CI config is playwright.config.ts.
export default defineConfig({
  testDir: './tests',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: process.env.DEV_BASE_URL || 'http://localhost:5599',
    trace: 'off',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
});
