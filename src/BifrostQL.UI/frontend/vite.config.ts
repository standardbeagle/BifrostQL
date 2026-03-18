import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react-swc';
import path from 'node:path';
import fs from 'node:fs';

// Workspace root (two levels above frontend/)
const workspaceRoot = path.resolve(__dirname, '../../..');
const editDbDist = path.resolve(workspaceRoot, 'examples/edit-db/dist');

/**
 * Vite plugin that watches a workspace library's dist/ directory and
 * triggers a full page reload when the library rebuilds. This is the
 * standard monorepo pattern: the library runs `vite build --watch`
 * and the consuming app picks up the rebuilt files.
 */
function watchWorkspaceLib(distDir: string) {
  return {
    name: 'watch-workspace-lib',
    configureServer(server: { watcher: fs.FSWatcher; ws: { send: (msg: { type: string }) => void } }) {
      // Watch the library's dist directory for changes
      server.watcher.add(distDir);
      server.watcher.on('change', (changedPath: string) => {
        if (changedPath.startsWith(distDir)) {
          // Invalidate the module graph and send full reload
          server.ws.send({ type: 'full-reload' });
        }
      });
    },
  };
}

export default defineConfig({
  plugins: [react(), watchWorkspaceLib(editDbDist)],
  resolve: {
    dedupe: ['react', 'react-dom', '@tanstack/react-query'],
  },
  optimizeDeps: {
    // Don't pre-bundle the workspace library so vite serves the actual
    // dist/ files and picks up changes from the watch build.
    exclude: ['@standardbeagle/edit-db'],
  },
  build: {
    outDir: path.resolve(__dirname, '../wwwroot'),
    emptyOutDir: true,
  },
  server: {
    fs: {
      // Allow serving files from workspace root (needed for /@fs/ paths
      // to the symlinked edit-db dist/).
      allow: [workspaceRoot],
    },
    proxy: {
      '/graphql': {
        target: 'http://localhost:5000',
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
