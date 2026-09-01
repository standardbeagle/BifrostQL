/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react-swc';

// Build output goes straight into the ASP.NET host's wwwroot so the sample runs
// with a single `dotnet run`. `dev` proxies /graphql and /_app-metadata to the
// host on port 5000.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/graphql': 'http://localhost:5000',
      '/_app-metadata': 'http://localhost:5000',
      '/auth': 'http://localhost:5000',
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test-setup.ts'],
    // Parallel jsdom workers on a busy machine can stretch a userEvent-heavy
    // test body past vitest's 5 s default; that kill reports "timed out", not
    // a real assertion failure. Must stay above the asyncUtilTimeout set in
    // src/test-setup.ts so waitFor errors surface with their DOM dump.
    testTimeout: 20000,
  },
});
