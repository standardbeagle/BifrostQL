import { describe, expect, it } from 'vitest';
import { getMultiJoinRows, clampPageIndex } from './useDataTable';
import type { Join } from '../types/schema';

describe('clampPageIndex', () => {
    it('snaps an out-of-range page back to the last valid page ("page 2 of 1")', () => {
        expect(clampPageIndex(1, 1)).toBe(0);
        expect(clampPageIndex(5, 3)).toBe(2);
    });

    it('leaves an in-range page untouched', () => {
        expect(clampPageIndex(0, 1)).toBe(0);
        expect(clampPageIndex(2, 5)).toBe(2);
    });

    it('never returns below zero, even for empty/zero page counts', () => {
        expect(clampPageIndex(3, 0)).toBe(0);
        expect(clampPageIndex(-1, 5)).toBe(0);
    });
});

describe('getMultiJoinRows', () => {
    it('reads aliased multi-join rows from fieldName instead of destinationTable', () => {
        const join: Join = {
            name: 'categories',
            fieldName: 'categories_children',
            sourceColumnNames: ['id'],
            destinationTable: 'categories',
            destinationColumnNames: ['parent_id'],
        };
        const row = {
            id: 1,
            categories: { data: [{ id: 99, name: 'wrong field' }] },
            categories_children: { data: [{ id: 2, name: 'Child' }] },
        };

        expect(getMultiJoinRows(row, join)).toEqual([{ id: 2, name: 'Child' }]);
    });

    it('falls back to destinationTable for legacy metadata without fieldName', () => {
        const join: Join = {
            name: 'orders',
            sourceColumnNames: ['id'],
            destinationTable: 'orders',
            destinationColumnNames: ['user_id'],
        };
        const row = {
            id: 1,
            orders: { data: [{ id: 10 }] },
        };

        expect(getMultiJoinRows(row, join)).toEqual([{ id: 10 }]);
    });
});
