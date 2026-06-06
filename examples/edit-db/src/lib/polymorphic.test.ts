import { describe, it, expect } from 'vitest';
import {
    buildChildDrillDownFilter,
    buildPolymorphicClause,
    findChildMultiJoin,
} from './polymorphic';
import type { Join } from '../types/schema';

describe('buildChildDrillDownFilter', () => {
    it('scopes a polymorphic drill-down by BOTH the id and the discriminator', () => {
        // Arrange: a polymorphic relationship (notes keyed by entity_type + entity_id)
        const rel = {
            isPolymorphic: true,
            polymorphicTypeColumn: 'entity_type',
            polymorphicTypeValue: 'company',
        };

        // Act
        const filter = buildChildDrillDownFilter('entity_id', rel);

        // Assert: both predicates present so other parents' rows cannot leak
        expect(filter).toContain('entity_id: { _eq: $id}');
        expect(filter).toContain('entity_type: {_eq: "company"}');
        expect(filter).toBe('{and: [{ entity_id: { _eq: $id}}, {entity_type: {_eq: "company"}}]}');
    });

    it('emits only the id predicate for a non-polymorphic relationship (unchanged)', () => {
        // Arrange: a plain FK relationship
        const rel = { isPolymorphic: false };

        // Act
        const filter = buildChildDrillDownFilter('customer_id', rel);

        // Assert: legacy single-column shape, no discriminator
        expect(filter).toBe('{ customer_id: { _eq: $id}}');
        expect(filter).not.toContain('and');
    });

    it('treats a relationship with no polymorphic info as non-polymorphic', () => {
        // Act
        const filter = buildChildDrillDownFilter('order_id', {});

        // Assert
        expect(filter).toBe('{ order_id: { _eq: $id}}');
    });

    it('does not add the discriminator when the type column is missing', () => {
        // Arrange: flagged polymorphic but discriminator metadata absent
        const filter = buildChildDrillDownFilter('entity_id', {
            isPolymorphic: true,
            polymorphicTypeValue: 'company',
        });

        // Assert: falls back to id-only rather than emit a broken clause
        expect(filter).toBe('{ entity_id: { _eq: $id}}');
    });

    it('honours a custom id variable name', () => {
        const filter = buildChildDrillDownFilter('entity_id', {
            isPolymorphic: true,
            polymorphicTypeColumn: 'entity_type',
            polymorphicTypeValue: 'deal',
        }, '$pk');

        expect(filter).toBe('{and: [{ entity_id: { _eq: $pk}}, {entity_type: {_eq: "deal"}}]}');
    });
});

describe('buildPolymorphicClause', () => {
    it('returns the discriminator clause for a polymorphic relationship', () => {
        expect(
            buildPolymorphicClause({
                isPolymorphic: true,
                polymorphicTypeColumn: 'entity_type',
                polymorphicTypeValue: 'contact',
            }),
        ).toBe('{entity_type: {_eq: "contact"}}');
    });

    it('returns null for a non-polymorphic relationship', () => {
        expect(buildPolymorphicClause({ isPolymorphic: false })).toBeNull();
        expect(buildPolymorphicClause({})).toBeNull();
    });

    it('escapes special characters in the discriminator value', () => {
        expect(
            buildPolymorphicClause({
                isPolymorphic: true,
                polymorphicTypeColumn: 'entity_type',
                polymorphicTypeValue: 'a"b',
            }),
        ).toBe('{entity_type: {_eq: "a\\"b"}}');
    });
});

describe('findChildMultiJoin', () => {
    const join = (over: Partial<Join>): Join => ({
        name: 'notes',
        sourceColumnNames: ['id'],
        destinationTable: 'notes',
        destinationColumnNames: ['entity_id'],
        ...over,
    });

    it('finds the multi-join targeting the child via the given id column', () => {
        const joins = [
            join({ destinationTable: 'orders', destinationColumnNames: ['customer_id'] }),
            join({
                destinationTable: 'notes',
                destinationColumnNames: ['entity_id'],
                isPolymorphic: true,
                polymorphicTypeColumn: 'entity_type',
                polymorphicTypeValue: 'company',
            }),
        ];

        const found = findChildMultiJoin(joins, 'notes', 'entity_id');

        expect(found?.isPolymorphic).toBe(true);
        expect(found?.polymorphicTypeValue).toBe('company');
    });

    it('returns undefined when no multi-join matches', () => {
        expect(findChildMultiJoin([join({})], 'notes', 'other_id')).toBeUndefined();
        expect(findChildMultiJoin(undefined, 'notes', 'entity_id')).toBeUndefined();
    });
});
