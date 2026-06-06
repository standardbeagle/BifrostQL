import { describe, it, expect } from 'vitest';
import { resolveChildJoin, childFieldName } from './polymorphic';
import type { Join } from '../types/schema';

const join = (over: Partial<Join>): Join => ({
    name: 'notes',
    sourceColumnNames: ['id'],
    destinationTable: 'notes',
    destinationColumnNames: ['entity_id'],
    ...over,
});

describe('resolveChildJoin', () => {
    it('returns the single multi-join that targets the child table', () => {
        const joins = [
            join({ destinationTable: 'orders', destinationColumnNames: ['customer_id'] }),
            join({ destinationTable: 'notes', destinationColumnNames: ['entity_id'] }),
        ];
        expect(resolveChildJoin(joins, 'notes')?.destinationTable).toBe('notes');
    });

    it('disambiguates by destination column when several joins target the same child', () => {
        const joins = [
            join({ fieldName: 'notes_by_a', destinationColumnNames: ['a_id'] }),
            join({ fieldName: 'notes_by_b', destinationColumnNames: ['b_id'] }),
        ];
        expect(resolveChildJoin(joins, 'notes', 'b_id')?.fieldName).toBe('notes_by_b');
    });

    it('falls back to the first match when the column does not disambiguate', () => {
        const joins = [
            join({ fieldName: 'notes_by_a', destinationColumnNames: ['a_id'] }),
            join({ fieldName: 'notes_by_b', destinationColumnNames: ['b_id'] }),
        ];
        expect(resolveChildJoin(joins, 'notes', 'unknown')?.fieldName).toBe('notes_by_a');
    });

    it('returns undefined when no multi-join matches', () => {
        expect(resolveChildJoin([join({ destinationTable: 'orders' })], 'notes')).toBeUndefined();
        expect(resolveChildJoin(undefined, 'notes')).toBeUndefined();
    });
});

describe('childFieldName', () => {
    it('prefers the join fieldName when present', () => {
        expect(childFieldName(join({ fieldName: 'categories_children' }))).toBe('categories_children');
    });

    it('falls back to the destination table when fieldName is absent', () => {
        expect(childFieldName(join({ destinationTable: 'notes' }))).toBe('notes');
    });
});
