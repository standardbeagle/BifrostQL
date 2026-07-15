import { describe, it, expect } from 'vitest';
import { nextIndex, canNavigate, positionLabel } from './form-runner-nav';

describe('nextIndex', () => {
  it('moves within bounds and clamps at the ends', () => {
    expect(nextIndex(0, 5, 'first')).toBe(0);
    expect(nextIndex(2, 5, 'first')).toBe(0);
    expect(nextIndex(2, 5, 'last')).toBe(4);
    expect(nextIndex(4, 5, 'next')).toBe(4); // already last
    expect(nextIndex(0, 5, 'prev')).toBe(0); // already first
    expect(nextIndex(2, 5, 'next')).toBe(3);
    expect(nextIndex(2, 5, 'prev')).toBe(1);
  });

  it('never yields a negative or out-of-range index on an empty table', () => {
    expect(nextIndex(0, 0, 'next')).toBe(0);
    expect(nextIndex(0, 0, 'last')).toBe(0);
    expect(nextIndex(3, 0, 'prev')).toBe(0);
  });
});

describe('canNavigate', () => {
  it('is false at the bounds it cannot leave', () => {
    expect(canNavigate(0, 5, 'prev')).toBe(false);
    expect(canNavigate(0, 5, 'first')).toBe(false);
    expect(canNavigate(4, 5, 'next')).toBe(false);
    expect(canNavigate(4, 5, 'last')).toBe(false);
    expect(canNavigate(2, 5, 'next')).toBe(true);
    expect(canNavigate(2, 5, 'first')).toBe(true);
  });
});

describe('positionLabel', () => {
  it('renders a 1-based position and the empty case', () => {
    expect(positionLabel(0, 12)).toBe('1 of 12');
    expect(positionLabel(2, 12)).toBe('3 of 12');
    expect(positionLabel(0, 0)).toBe('0 of 0');
  });
});
