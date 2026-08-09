import { defineConfig } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

const serverUrlFile = path.join(__dirname, '.server-url');

// This config is loaded twice: once by the runner BEFORE globalSetup writes
// .server-url, and again by each worker after it exists. Only the worker's value
// is used by tests, so a missing file there means the suite would silently target
// whatever else is listening — port 5000 is dev-ui.sh's default, i.e. a
// developer's own dev server. Fail fast in workers instead of adopting a stranger.
function getBaseURL(): string | undefined {
  if (fs.existsSync(serverUrlFile)) {
    return fs.readFileSync(serverUrlFile, 'utf-8').trim();
  }
  if (process.env.TEST_WORKER_INDEX !== undefined) {
    throw new Error(
      `${serverUrlFile} is missing — globalSetup did not start a server. ` +
      `Refusing to run against an unknown server.`,
    );
  }
  return undefined;
}

export default defineConfig({
  testDir: './tests',
  timeout: 60_000,
  // Hard ceiling on the whole run. Without it a hung suite holds the server, the
  // browsers and a CI agent indefinitely; with it the runner exits and teardown
  // reaps the server group.
  globalTimeout: 20 * 60_000,
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
