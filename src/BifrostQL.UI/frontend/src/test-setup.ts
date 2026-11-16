import { configure } from '@testing-library/react';

// Every await in these tests is a condition that must eventually hold, so the
// deadline only has to catch a hang, not measure speed. On a loaded machine
// (parallel jsdom workers plus outside load) a render can need well over the
// library's 1000 ms default. Kept below the vitest `testTimeout` so a failed
// `waitFor` still reports its own DOM dump instead of a bare timeout kill.
configure({ asyncUtilTimeout: 4000 });
