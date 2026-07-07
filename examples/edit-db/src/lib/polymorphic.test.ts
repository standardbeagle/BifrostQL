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

    it('returns undefined when the idColumn matches no candidate (no silent wrong pick)', () => {
        // Regression: used to fall back to matches[0], silently scoping the query to
        // the wrong FK and returning another relationship's children.
        const joins = [
            join({ fieldName: 'notes_by_a', destinationColumnNames: ['a_id'] }),
            join({ fieldName: 'notes_by_b', destinationColumnNames: ['b_id'] }),
        ];
        expect(resolveChildJoin(joins, 'notes', 'unknown')).toBeUndefined();
    });

    it('returns undefined when several joins match and no idColumn disambiguates', () => {
        const joins = [
            join({ fieldName: 'notes_by_a', destinationColumnNames: ['a_id'] }),
            join({ fieldName: 'notes_by_b', destinationColumnNames: ['b_id'] }),
        ];
        expect(resolveChildJoin(joins, 'notes')).toBeUndefined();
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
