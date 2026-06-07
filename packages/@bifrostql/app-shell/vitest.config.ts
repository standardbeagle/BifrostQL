import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react-swc';
import path from 'node:path';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@bifrostql/react/server': path.resolve(
        __dirname,
        '../react/src/server/index.ts',
      ),
      '@bifrostql/react': path.resolve(__dirname, '../react/src/index.ts'),
      '@bifrostql/types/generated': path.resolve(
        __dirname,
        '../types/src/generated/index.ts',
      ),
      '@bifrostql/types': path.resolve(__dirname, '../types/src/index.ts'),
    },
  },
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: ['./src/test-setup.ts'],
    coverage: {
      provider: 'v8',
      reporter: ['text', 'lcov', 'json-summary'],
      include: ['src/**/*.{ts,tsx}'],
      exclude: [
        'src/test-setup.ts',
        'src/vite-env.d.ts',
        'src/**/*.test.{ts,tsx}',
      ],
      thresholds: {
        branches: 80,
        functions: 80,
        lines: 80,
        statements: 80,
      },
    },
  },
});
