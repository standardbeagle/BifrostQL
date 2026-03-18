import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react-swc';
import path from 'node:path';

// Workspace root (two levels above frontend/)
const workspaceRoot = path.resolve(__dirname, '../../..');

export default defineConfig({
  plugins: [react()],
  resolve: {
    dedupe: ['react', 'react-dom', '@tanstack/react-query'],
  },
  optimizeDeps: {
    // Don't pre-bundle the workspace library — serve its dist/ files directly
    // so that rebuilds are picked up immediately without stale cache.
    exclude: ['@standardbeagle/edit-db'],
  },
  build: {
    outDir: path.resolve(__dirname, '../wwwroot'),
    emptyOutDir: true,
  },
  server: {
    fs: {
      // Allow serving files from the workspace root (needed for /@fs/ imports
      // of the symlinked edit-db dist/).
      allow: [workspaceRoot],
    },
    watch: {
      // Watch the edit-db dist directory for rebuilds.
      // WSL2 inotify can be unreliable for cross-filesystem symlinks, so poll.
      usePolling: true,
      interval: 500,
    },
    proxy: {
      '/graphql': {
        target: 'http://localhost:5000',
        // Suppress noisy ECONNREFUSED errors during backend startup
        configure: (proxy) => {
          proxy.on('error', () => {});
        },
      },
      '/api': {
        target: 'http://localhost:5000',
        configure: (proxy) => {
          proxy.on('error', () => {});
        },
      },
    },
  },
});
