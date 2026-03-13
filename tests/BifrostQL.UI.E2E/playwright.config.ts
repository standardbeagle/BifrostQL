import { defineConfig } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

const serverUrlFile = path.join(__dirname, '.server-url');

function getBaseURL(): string {
  if (fs.existsSync(serverUrlFile)) {
    return fs.readFileSync(serverUrlFile, 'utf-8').trim();
  }
  return 'http://localhost:5000';
}

export default defineConfig({
  testDir: './tests',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,  // tests share a server, run sequentially
  workers: 1,            // single worker — server has global connection state
  retries: 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  globalSetup: './global-setup.ts',
  globalTeardown: './global-teardown.ts',
  use: {
    baseURL: getBaseURL(),
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: { browserName: 'chromium' },
    },
  ],
});
