import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react-swc';
import path from 'node:path';

export default defineConfig({
  plugins: [react()],
  resolve: {
    dedupe: ['react', 'react-dom', '@tanstack/react-query'],
  },
  build: {
    outDir: path.resolve(__dirname, '../wwwroot'),
    emptyOutDir: true,
  },
  server: {
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
