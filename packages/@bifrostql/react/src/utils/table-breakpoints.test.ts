import { describe, it, expect } from 'vitest';
import {
  DEFAULT_BREAKPOINTS,
  getBreakpointFromWidth,
  getColumnsForBreakpoint,
  resolveClientSideFilterConfig,
  resolveClientSideSortConfig,
  resolveUrlSyncConfig,
} from './table-breakpoints';

describe('getBreakpointFromWidth', () => {
  it('maps widths to the largest matching breakpoint', () => {
    expect(getBreakpointFromWidth(0, DEFAULT_BREAKPOINTS)).toBe('xs');
    expect(getBreakpointFromWidth(700, DEFAULT_BREAKPOINTS)).toBe('sm');
    expect(getBreakpointFromWidth(800, DEFAULT_BREAKPOINTS)).toBe('md');
    expect(getBreakpointFromWidth(1100, DEFAULT_BREAKPOINTS)).toBe('lg');
    expect(getBreakpointFromWidth(2000, DEFAULT_BREAKPOINTS)).toBe('xl');
  });
});

describe('getColumnsForBreakpoint', () => {
  const all = ['a', 'b', 'c'];

  it('returns all columns when no responsive config is provided', () => {
    expect(getColumnsForBreakpoint(all, undefined, 'xs')).toBe(all);
    expect(getColumnsForBreakpoint(all, [], 'xs')).toBe(all);
  });

  it('hides columns below their minimum breakpoint', () => {
    const config = [
      { field: 'c', priority: 1 as const, minBreakpoint: 'md' as const },
    ];
    expect(getColumnsForBreakpoint(all, config, 'xs')).toEqual(['a', 'b']);
    expect(getColumnsForBreakpoint(all, config, 'lg')).toEqual(['a', 'b', 'c']);
  });
});

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
