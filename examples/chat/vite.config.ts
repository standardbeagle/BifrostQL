import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react-swc';

// The dev server proxies both the GraphQL endpoint and the chat SSE endpoints
// to a running BifrostQL host so the SPA stays same-origin (cookies included).
// Override the host with BIFROST_URL, e.g.:
//   BIFROST_URL=http://localhost:5000 pnpm --dir examples/chat dev
const bifrostUrl = process.env.BIFROST_URL ?? 'http://localhost:5077';

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/graphql': { target: bifrostUrl, changeOrigin: true },
      '/_chat': { target: bifrostUrl, changeOrigin: true },
    },
  },
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
  },
});
