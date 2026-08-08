import { describe, it, expect } from 'vitest';
import { resolveDisplayFormat, formatRelative, formatDateCellValue, abbreviateNumber } from './format-value';
import type { Column } from '../types/schema';

function col(partial: Partial<Column>): Column {
    return { name: 'c', paramType: '', dbType: '', metadata: {}, ...partial } as Column;
}

describe('resolveDisplayFormat', () => {
    it('honors an explicit display-format metadata value (case-insensitive)', () => {
        expect(resolveDisplayFormat(col({ metadata: { 'display-format': 'relative' } }))).toBe('relative');
        expect(resolveDisplayFormat(col({ metadata: { 'display-format': 'RELATIVE' } }))).toBe('relative');
    });

    it('ignores unknown metadata values and falls back to type inference', () => {
        expect(resolveDisplayFormat(col({ metadata: { 'display-format': 'bogus' }, dbType: 'nvarchar' }))).toBeNull();
    });

    it('infers datetime from datetime-ish db/param types', () => {
        expect(resolveDisplayFormat(col({ paramType: 'DateTime' }))).toBe('datetime');
        expect(resolveDisplayFormat(col({ dbType: 'datetime2' }))).toBe('datetime');
        expect(resolveDisplayFormat(col({ dbType: 'datetimeoffset' }))).toBe('datetime');
        expect(resolveDisplayFormat(col({ paramType: 'DateTimeOffset' }))).toBe('datetime');
    });

    it('infers date from date types', () => {
        expect(resolveDisplayFormat(col({ paramType: 'Date' }))).toBe('date');
        expect(resolveDisplayFormat(col({ dbType: 'date' }))).toBe('date');
    });

    it('returns null for plain string/number columns', () => {
        expect(resolveDisplayFormat(col({ paramType: 'String', dbType: 'nvarchar' }))).toBeNull();
        expect(resolveDisplayFormat(col({ paramType: 'Int', dbType: 'int' }))).toBeNull();
    });

    it('strips the non-null ! suffix when matching the param type', () => {
        expect(resolveDisplayFormat(col({ paramType: 'DateTime!' }))).toBe('datetime');
    });
});

describe('formatRelative', () => {
    const now = new Date('2026-06-17T12:00:00Z');

    it('formats past times', () => {
        expect(formatRelative(new Date('2026-06-17T08:00:00Z'), now)).toBe('4 hours ago');
        expect(formatRelative(new Date('2026-06-16T12:00:00Z'), now)).toBe('yesterday');
        expect(formatRelative(new Date('2026-06-10T12:00:00Z'), now)).toBe('last week');
    });

    it('formats future times', () => {
        expect(formatRelative(new Date('2026-06-17T15:00:00Z'), now)).toBe('in 3 hours');
        expect(formatRelative(new Date('2026-06-20T12:00:00Z'), now)).toBe('in 3 days');
    });
});

describe('formatDateCellValue', () => {
    it('returns empty for empty string', () => {
        expect(formatDateCellValue('')).toBe('');
    });

    it('returns empty for invalid date', () => {
        expect(formatDateCellValue('not-a-date')).toBe('');
    });

    it('returns empty only for exact sentinel/placeholder instants', () => {
        // Unix epoch (timestamp 0) and year-0001 min-value are "unset" markers.
        expect(formatDateCellValue('1970-01-01')).toBe('');
        expect(formatDateCellValue('0001-01-01')).toBe('');
    });

    it('formats legitimate historical dates (no 1973 cutoff)', () => {
        // Previously blanked by the broad "before 1973" cutoff; these are real dates.
        expect(formatDateCellValue('1900-06-15')).toBeTruthy();
        expect(formatDateCellValue('1969-07-20')).toBeTruthy();
    });

    it('formats valid modern dates', () => {
        const result = formatDateCellValue('2024-06-15T10:30:00');
        expect(result).toBeTruthy();
        expect(result.length).toBeGreaterThan(5);
    });

    it('handles ISO date strings', () => {
        expect(formatDateCellValue('2024-01-01')).toBeTruthy();
    });

    it('handles null-like values', () => {
        expect(formatDateCellValue(null as unknown as string)).toBe('');
        expect(formatDateCellValue(undefined as unknown as string)).toBe('');
    });
});

describe('abbreviateNumber', () => {
  it('returns em dash for null', () => {
    expect(abbreviateNumber(null)).toBe('—');
  });

  it('returns 0 for zero', () => {
    expect(abbreviateNumber(0)).toBe('0');
  });

  it('returns number as string for values under 1000', () => {
    expect(abbreviateNumber(12)).toBe('12');
    expect(abbreviateNumber(999)).toBe('999');
  });

  it('returns k format for thousands', () => {
    expect(abbreviateNumber(1200)).toBe('1.2k');
    expect(abbreviateNumber(45000)).toBe('45k');
  });

  it('returns M format for millions', () => {
    expect(abbreviateNumber(1200000)).toBe('1.2M');
  });

  it('handles exact thousands without decimal', () => {
    expect(abbreviateNumber(1000)).toBe('1k');
  });

  it('handles exact millions without decimal', () => {
    expect(abbreviateNumber(1000000)).toBe('1M');
  });
});
