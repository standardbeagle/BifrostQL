import { describe, it, expect } from 'vitest';
import {
  escapeCsvValue,
  formatExportValue,
  rowsToCsv,
  rowsToTsv,
  rowsToJson,
} from './table-export';

describe('escapeCsvValue', () => {
  it('returns empty string for null and undefined', () => {
    expect(escapeCsvValue(null)).toBe('');
    expect(escapeCsvValue(undefined)).toBe('');
  });

  it('leaves plain values unquoted', () => {
    expect(escapeCsvValue('hello')).toBe('hello');
    expect(escapeCsvValue(42)).toBe('42');
  });

  it('quotes and escapes values containing commas, quotes, or newlines', () => {
    expect(escapeCsvValue('a,b')).toBe('"a,b"');
    expect(escapeCsvValue('say "hi"')).toBe('"say ""hi"""');
    expect(escapeCsvValue('line1\nline2')).toBe('"line1\nline2"');
    expect(escapeCsvValue('carriage\rreturn')).toBe('"carriage\rreturn"');
  });
});

describe('formatExportValue', () => {
  it('prefers a field-specific formatter when present', () => {
    const formatters = {
      price: (v: unknown) => `$${v}`,
    };
    expect(formatExportValue(10, 'price', { price: 10 }, formatters)).toBe(
      '$10',
    );
  });

  it('falls back to String for values without a formatter', () => {
    expect(formatExportValue(5, 'qty', { qty: 5 })).toBe('5');
    expect(formatExportValue(null, 'qty', {})).toBe('');
    expect(formatExportValue(undefined, 'qty', {})).toBe('');
  });
});

describe('rowsToCsv', () => {
  it('builds a header line plus escaped data rows', () => {
    const rows = [
      { id: 1, name: 'Ann' },
      { id: 2, name: 'Bo,b' },
    ];
    const csv = rowsToCsv(rows, ['id', 'name'], ['ID', 'Name']);
    expect(csv).toBe('ID,Name\n1,Ann\n2,"Bo,b"');
  });
});

describe('rowsToTsv', () => {
  it('joins with tabs and replaces embedded tabs with spaces', () => {
    const rows = [{ a: 'x\ty', b: 'z' }];
    const tsv = rowsToTsv(rows, ['a', 'b'], ['A', 'B']);
    expect(tsv).toBe('A\tB\nx y\tz');
  });
});

describe('rowsToJson', () => {
  it('projects only the requested fields, applying formatters', () => {
    const rows = [{ id: 1, secret: 'x', name: 'Ann' }];
    const json = rowsToJson(rows, ['id', 'name'], {
      name: (v: unknown) => String(v).toUpperCase(),
    });
    expect(JSON.parse(json)).toEqual([{ id: 1, name: 'ANN' }]);
  });
});
