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
  },
});
