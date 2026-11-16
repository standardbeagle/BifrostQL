import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import { formatColumnValue } from './format-value';
import type { Column } from '../types/schema';

function col(partial: Partial<Column>): Column {
    return { name: 'c', paramType: '', dbType: '', metadata: {}, ...partial } as Column;
}

describe('formatColumnValue (render)', () => {
    const statusColumn = (partial: Partial<Column> = {}) => col({
        paramType: 'Int',
        dbType: 'int',
        enumValues: ['0', '1', '2', '3'],
        enumLabels: ['Active', 'Audit', 'Dropped', 'Pregnant'],
        ...partial,
    });

    it('shows the enum label for a numeric code, keeping the stored value in title', () => {
        const { container } = render(<>{formatColumnValue(2, statusColumn())}</>);
        const span = container.querySelector('span')!;
        expect(span.textContent).toBe('Dropped');
        expect(span.getAttribute('title')).toBe('2');
    });

    it('labels a code of 0, which is falsy but a real enum value', () => {
        const { container } = render(<>{formatColumnValue(0, statusColumn())}</>);
        expect(container.textContent).toBe('Active');
    });

    it('shows the raw value when labels do not line up with values', () => {
        const { container } = render(<>{formatColumnValue(1, statusColumn({ enumLabels: ['Active', 'Audit'] }))}</>);
        expect(container.textContent).toBe('1');
    });

    it('shows the raw value for a code outside the enum values', () => {
        const { container } = render(<>{formatColumnValue(9, statusColumn())}</>);
        expect(container.textContent).toBe('9');
    });

    it('renders a SQL Server datetime2 concisely with the exact value in title', () => {
        const { container } = render(
            <>{formatColumnValue('2026-05-11T22:17:47.7636626', col({ dbType: 'datetime2' }))}</>
        );
        const span = container.querySelector('span')!;
        // No raw ISO abomination in the visible text...
        expect(span.textContent).not.toContain('7636626');
        expect(span.textContent).not.toContain('T22');
        // ...but the exact value stays reachable on hover.
        expect(span.getAttribute('title')).toBeTruthy();
    });

    it('renders a plain string column unchanged (no formatting)', () => {
        const { container } = render(<>{formatColumnValue('hello', col({ dbType: 'nvarchar' }))}</>);
        expect(container.textContent).toBe('hello');
        expect(container.querySelector('span')).toBeNull();
    });

    it('renders a relative format with hover-to-exact title', () => {
        const { container } = render(
            <>{formatColumnValue('2026-05-11T22:17:47Z', col({ dbType: 'datetime2', metadata: { 'display-format': 'relative' } }))}</>
        );
        const span = container.querySelector('span')!;
        expect(span.getAttribute('title')).toBeTruthy();
        // Relative phrasing, not a raw timestamp.
        expect(span.textContent).toMatch(/ago|in |yesterday|last|next|month|week|day|hour/);
        expect(span.textContent).not.toContain('2026');
    });

    it('groups a number when display-format=number', () => {
        const { container } = render(
            <>{formatColumnValue(1234567, col({ paramType: 'Int', metadata: { 'display-format': 'number' } }))}</>
        );
        // grouping separator is locale-dependent; just assert it is no longer the bare digits
        expect(container.textContent).not.toBe('1234567');
        expect(container.textContent?.replace(/\D/g, '')).toBe('1234567');
    });

    it('falls back to raw text for an unparseable date', () => {
        const { container } = render(<>{formatColumnValue('not-a-date', col({ dbType: 'datetime2' }))}</>);
        expect(container.textContent).toBe('not-a-date');
    });

    // Placeholder instants mean "not set". They render as a visible placeholder,
    // never as a blank cell, and the stored value stays reachable on hover.
    it.each([
        ['epoch, nullable DateTime', '1970-01-01T00:00:00Z', 'DateTime'],
        ['year 0001, NOT NULL DateTime', '0001-01-01T00:00:00', 'DateTime!'],
        ['year 0001, DateTimeOffset', '0001-01-01T00:00:00+00:00', 'DateTimeOffset'],
    ])('shows an unset placeholder for the %s sentinel', (_name, value, paramType) => {
        const { container } = render(<>{formatColumnValue(value, col({ paramType, dbType: 'datetime2' }))}</>);
        const span = container.querySelector('span')!;
        expect(span.textContent).toBe('unset');
        expect(span.getAttribute('title')).toBe(value);
    });

    it('formats real historical dates near the sentinels', () => {
        for (const value of ['1900-06-15T00:00:00Z', '1969-07-20T20:17:00Z', '1970-01-02T00:00:00Z']) {
            const { container } = render(<>{formatColumnValue(value, col({ paramType: 'DateTime' }))}</>);
            expect(container.textContent).not.toBe('unset');
            expect(container.textContent).not.toBe('');
        }
    });

    it('honors display-format on a nullable DateTime column the same as a NOT NULL one', () => {
        const now = new Date(Date.now() - 3 * 3600_000).toISOString();
        for (const paramType of ['DateTime', 'DateTime!']) {
            const { container } = render(<>{formatColumnValue(now, col({ paramType, metadata: { 'display-format': 'relative' } }))}</>);
            expect(container.textContent).toBe('3 hours ago');
        }
    });

    it.each([
        [true, 'true'],
        [false, 'false'],
    ])('renders boolean %s as a glyph with the value as hover and screen-reader text', (value, text) => {
        const { container } = render(<>{formatColumnValue(value, col({ paramType: 'Boolean!' }))}</>);
        const outer = container.firstElementChild!;
        expect(outer.getAttribute('title')).toBe(text);
        expect(outer.querySelector('svg')).not.toBeNull();
        expect(outer.querySelector('.sr-only')!.textContent).toBe(text);
    });

    it('renders a non-boolean value in a Boolean column raw, not as false', () => {
        const { container } = render(<>{formatColumnValue(2, col({ paramType: 'Boolean' }))}</>);
        expect(container.textContent).toBe('2');
        expect(container.querySelector('svg')).toBeNull();
    });
});
