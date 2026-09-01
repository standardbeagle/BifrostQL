import '@testing-library/jest-dom/vitest';
import { configure } from '@testing-library/react';

// The suite runs 29 jsdom files across parallel workers, so on a loaded
// machine a single `waitFor` can legitimately need more than the library's
// 1000 ms default before the awaited render settles. Every await in these
// tests is a condition that must eventually hold — the deadline is purely
// environmental — so give it headroom instead of letting CPU contention
// read as a test failure. Kept below the vitest `testTimeout` in
// vite.config.ts so a genuinely failed `waitFor` still reports its own
// DOM-dump error rather than a bare test-timeout kill.
configure({ asyncUtilTimeout: 4000 });
