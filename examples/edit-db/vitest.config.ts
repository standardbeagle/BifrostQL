import { defineConfig } from 'vitest/config';
import path from 'node:path';

export default defineConfig({
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    include: ['src/**/*.test.{ts,tsx}'],
    // Node 25+ defines its own global localStorage, which is undefined without
    // --localstorage-file. jsdom's window.localStorage is not copied over a global
    // that already exists, so every storage test saw undefined on Node 26.
    execArgv: ['--no-experimental-webstorage'],
    setupFiles: ['./src/test-setup.ts'],
    // A hang detector, not a speed check: under machine load a jsdom test body
    // can run 10x slower than on an idle box. Stays above the asyncUtilTimeout
    // in src/test-setup.ts so waitFor failures surface with their DOM dump.
    testTimeout: 20000,
  },
});
