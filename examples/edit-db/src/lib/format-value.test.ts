import { afterEach, describe, it, expect, vi } from 'vitest';
import {
    resolveDisplayFormat, formatRelative, abbreviateNumber,
    columnFormatter, formatColumnValue, enumLabelFor,
} from './format-value';
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

describe('cell formatting cost', () => {
    afterEach(() => vi.restoreAllMocks());

    it('builds no Intl.NumberFormat per number or percent cell', () => {
        const number = col({ paramType: 'Int', metadata: { 'display-format': 'number' } });
        const percent = col({ paramType: 'Float', metadata: { 'display-format': 'percent' } });
        columnFormatter(number);
        columnFormatter(percent);
        const ctor = vi.spyOn(Intl, 'NumberFormat');

        for (let i = 0; i < 50; i++) {
            formatColumnValue(i * 1000, number);
            formatColumnValue(i / 100, percent);
        }

        expect(ctor).not.toHaveBeenCalled();
    });

    it('formats date cells without building an Intl.DateTimeFormat per cell', () => {
        const nullable = col({ paramType: 'DateTime', dbType: 'datetime2' });
        const notNull = col({ paramType: 'DateTime!', dbType: 'datetime2' });
        columnFormatter(nullable);
        columnFormatter(notNull);
        const ctor = vi.spyOn(Intl, 'DateTimeFormat');
        const toLocale = vi.spyOn(Date.prototype, 'toLocaleString');

        for (const v of ['2024-06-15T10:30:00', '1999-12-31T23:59:59Z', '1900-06-15', '2026-05-11T22:17:47.7636626']) {
            formatColumnValue(v, nullable);
            formatColumnValue(v, notNull);
        }

        expect(ctor).not.toHaveBeenCalled();
        expect(toLocale).not.toHaveBeenCalled();
    });

    it('builds a column formatter once and reuses it', () => {
        const c = col({ paramType: 'Int', metadata: { 'display-format': 'number' } });
        expect(columnFormatter(c)).toBe(columnFormatter(c));
        expect(columnFormatter(c, 'display-format-preview')).not.toBe(columnFormatter(c));
    });
});

describe('enumLabelFor', () => {
    const status = (labels: string[]) =>
        col({ enumValues: ['0', '1', '0'], enumLabels: labels } as Partial<Column>);

    it('takes the first occurrence of a repeated value', () => {
        expect(enumLabelFor(0, status(['Draft', 'Active', 'Other']))).toBe('Draft');
    });

    it('treats an empty label as no label, even when a repeat has one', () => {
        expect(enumLabelFor(0, status(['', 'Active', 'Other']))).toBeNull();
        expect(enumLabelFor(1, status(['', 'Active', 'Other']))).toBe('Active');
    });

    it('has no labels when the counts disagree', () => {
        expect(enumLabelFor(1, col({ enumValues: ['0', '1'], enumLabels: ['Draft'] } as Partial<Column>))).toBeNull();
    });
});
