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
        'bifrost-app-shell': path.resolve(__dirname, 'src/index.ts'),
      },
      formats: ['es', 'cjs'],
      fileName: (format) => {
        const ext = format === 'cjs' ? 'cjs' : 'js';
        return `bifrost-app-shell.${ext}`;
      },
    },
    rollupOptions: {
      external: [
        'react',
        'react-dom',
        '@tanstack/react-query',
        '@bifrostql/react',
      ],
      output: {
        globals: {
          react: 'React',
          'react-dom': 'ReactDOM',
          '@tanstack/react-query': 'ReactQuery',
          '@bifrostql/react': 'BifrostReact',
        },
      },
    },
  },
});
