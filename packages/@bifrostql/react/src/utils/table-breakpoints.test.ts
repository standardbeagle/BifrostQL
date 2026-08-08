import { describe, it, expect } from 'vitest';
import {
  DEFAULT_BREAKPOINTS,
  getBreakpointFromWidth,
  getColumnsForBreakpoint,
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
