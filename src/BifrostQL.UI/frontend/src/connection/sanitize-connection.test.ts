import { describe, expect, it } from 'vitest';
import { parsePort } from './sanitize-connection';

describe('parsePort', () => {
  it('accepts exact integer ports in range', () => {
    expect(parsePort('5432')).toBe(5432);
    expect(parsePort(' 3306 ')).toBe(3306);
    expect(parsePort(1433)).toBe(1433);
  });

  it('rejects partial, decimal, empty, and out-of-range ports', () => {
    expect(parsePort('5432abc')).toBeUndefined();
    expect(parsePort('12.5')).toBeUndefined();
    expect(parsePort('')).toBeUndefined();
    expect(parsePort('0')).toBeUndefined();
    expect(parsePort('65536')).toBeUndefined();
  });
});
