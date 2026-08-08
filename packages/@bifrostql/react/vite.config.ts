import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react-swc';
import path from 'node:path';
import dts from 'vite-plugin-dts';

export default defineConfig({
  plugins: [
    react(),
    dts({
      insertTypesEntry: true,
      exclude: ['src/**/*.test.{ts,tsx}', 'src/test-setup.ts'],
    }),
  ],
  build: {
    lib: {
      entry: {
        'bifrost-react': path.resolve(__dirname, 'src/index.ts'),
        server: path.resolve(__dirname, 'src/server/index.ts'),
      },
      formats: ['es', 'cjs'],
      fileName: (format, entryName) => {
        const ext = format === 'cjs' ? 'cjs' : 'js';
        if (entryName === 'server') return `bifrost-react-server.${ext}`;
        return `bifrost-react.${ext}`;
      },
    },
    rollupOptions: {
      external: ['react', 'react-dom', '@tanstack/react-query'],
      output: {
        globals: {
          react: 'React',
          'react-dom': 'ReactDOM',
          '@tanstack/react-query': 'ReactQuery',
        },
      },
    },
  },
});
