import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { render } from '@testing-library/react';
import type { Column } from '../types/schema';

// The formatters build their Intl objects at import, which fixes the time zone
// then: pin a UTC-negative zone first and import the module after.
const savedTz = process.env.TZ;
let formatColumnValue: typeof import('./format-value').formatColumnValue;

beforeAll(async () => {
    process.env.TZ = 'America/Los_Angeles';
    vi.resetModules();
    ({ formatColumnValue } = await import('./format-value'));
});

afterAll(() => {
    if (savedTz === undefined) delete process.env.TZ; else process.env.TZ = savedTz;
    vi.resetModules();
});

function dateColumn(): Column {
    return { name: 'd', paramType: 'DateTime', dbType: 'date', metadata: { 'display-format': 'date' } } as unknown as Column;
}

function shown(value: unknown): string {
    return render(<>{formatColumnValue(value, dateColumn())}</>).container.textContent ?? '';
}

describe('date-only values west of UTC', () => {
    it('shows the calendar day a date column sent, not the day before', () => {
        const expected = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeZone: 'UTC' }).format(Date.UTC(2026, 4, 11));
        expect(shown('2026-05-11')).toBe(expected);
    });

    it('keeps the date-only epoch and year-0001 markers as unset, and a real 1969 day as data', () => {
        expect(shown('1970-01-01')).toBe('unset');
        expect(shown('0001-01-01')).toBe('unset');
        expect(shown('1969-12-31')).not.toBe('unset');
        expect(shown('1970-01-02')).not.toBe('unset');
    });
});
