import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import { formatColumnValue } from './format-value';
import type { Column } from '../types/schema';

function col(partial: Partial<Column>): Column {
    return { name: 'c', paramType: '', dbType: '', metadata: {}, ...partial } as Column;
}

describe('formatColumnValue (render)', () => {
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
});
