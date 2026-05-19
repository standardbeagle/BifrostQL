import { describe, expect, it } from 'vitest';
import { getMultiJoinRows } from './useDataTable';
import type { Join } from '../types/schema';

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
            categories: [{ id: 99, name: 'wrong field' }],
            categories_children: [{ id: 2, name: 'Child' }],
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
            orders: [{ id: 10 }],
        };

        expect(getMultiJoinRows(row, join)).toEqual([{ id: 10 }]);
    });
});
