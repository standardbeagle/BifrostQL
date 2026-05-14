import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    // The barrel has no runtime exports (types only), so the contract test in
    // src/index.test.ts asserts the re-export shape at the type level. Enabling
    // typecheck makes `expectTypeOf` assertions fail the run on a missing or
    // renamed export rather than silently passing.
    typecheck: {
      enabled: true,
      tsconfig: './tsconfig.test.json',
      include: ['src/**/*.test.ts'],
    },
    coverage: {
      provider: 'v8',
      reporter: ['text', 'lcov', 'json-summary'],
      include: ['src/**/*.ts'],
      exclude: ['src/**/*.test.ts'],
    },
  },
});
