import { describe, it, expect } from 'vitest';
import {
  resolveClientSideFilterConfig,
  resolveClientSideSortConfig,
  resolveUrlSyncConfig,
} from './table-option-resolvers';

describe('resolveUrlSyncConfig', () => {
  it('handles boolean and object forms', () => {
    expect(resolveUrlSyncConfig(false).enabled).toBe(false);
    expect(resolveUrlSyncConfig(true)).toEqual({
      enabled: true,
      prefix: 'table',
      debounceMs: 500,
    });
    expect(resolveUrlSyncConfig(undefined).enabled).toBe(true);
    expect(resolveUrlSyncConfig({ prefix: 'q', debounceMs: 100 })).toEqual({
      enabled: true,
      prefix: 'q',
      debounceMs: 100,
    });
  });
});

describe('resolveClientSideSortConfig / resolveClientSideFilterConfig', () => {
  it('treats true as unbounded and false/undefined as disabled', () => {
    expect(resolveClientSideSortConfig(true)).toEqual({
      enabled: true,
      threshold: Infinity,
    });
    expect(resolveClientSideSortConfig(false)).toEqual({
      enabled: false,
      threshold: 0,
    });
    expect(resolveClientSideSortConfig(undefined)).toEqual({
      enabled: false,
      threshold: 0,
    });
    expect(
      resolveClientSideFilterConfig({ enabled: true, threshold: 50 }),
    ).toEqual({ enabled: true, threshold: 50 });
  });
});
