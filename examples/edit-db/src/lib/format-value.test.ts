import { describe, it, expect } from 'vitest';
import { resolveDisplayFormat, formatRelative } from './format-value';
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
