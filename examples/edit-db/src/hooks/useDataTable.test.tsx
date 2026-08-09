import { describe, expect, it } from 'vitest';
import { getMultiJoinRows, getSingleJoinRow, clampPageIndex, getJoinedRowPkValue, reconcileColumnFiltersFromUrl } from './useDataTable';
import { serializeColumnFilters } from '../lib/query-builder';
import type { ColumnFiltersState } from '@tanstack/react-table';
import type { Join, Table } from '../types/schema';

describe('reconcileColumnFiltersFromUrl', () => {
    const cf = (id: string, operator: string, value: unknown): ColumnFiltersState =>
        [{ id, value: { operator, value } }];

    it('deserializes the URL cf param into filter state (back/forward restore)', () => {
        const cfParam = serializeColumnFilters(cf('name', '_contains', 'ann'));
        expect(reconcileColumnFiltersFromUrl([], cfParam)).toEqual(cf('name', '_contains', 'ann'));
    });

    it('clears filters when the URL drops cf (table switch)', () => {
        const prev = cf('name', '_contains', 'ann');
        expect(reconcileColumnFiltersFromUrl(prev, '')).toEqual([]);
    });

    it('returns the SAME reference when state already matches the URL (no loop, no re-render)', () => {
        const prev = cf('age', '_gte', 18);
        const cfParam = serializeColumnFilters(prev);
        expect(reconcileColumnFiltersFromUrl(prev, cfParam)).toBe(prev);
    });

    it('replaces state when the URL cf differs (divergence resolves toward the URL)', () => {
        const prev = cf('name', '_contains', 'old');
        const cfParam = serializeColumnFilters(cf('name', '_contains', 'new'));
        const next = reconcileColumnFiltersFromUrl(prev, cfParam);
        expect(next).not.toBe(prev);
        expect(next).toEqual(cf('name', '_contains', 'new'));
    });

    it('drops stale filters on a table switch even when the column name is shared', () => {
        // useDataTable derives the filters that DRIVE the query from this helper
        // (against the URL cf, the source of truth) during render, not from the
        // lagging reconciled state. On a table switch the URL drops cf, so even a
        // filter on a column name that also exists on the new table (id, name,
        // created_at, ...) must resolve to empty immediately — otherwise it would
        // leak into the new table's query and fire one redundant, wrong fetch
        // before the post-commit effect settled the state.
        const stale = cf('name', '_contains', 'left-over');
        expect(reconcileColumnFiltersFromUrl(stale, '')).toEqual([]);
    });
});

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

describe('getJoinedRowPkValue', () => {
    const tbl = (primaryKeys: string[]): Table => ({
        dbName: 't', graphQlName: 't', name: 't', label: 't', labelColumn: 'name',
        primaryKeys, isEditable: true, metadata: {}, columns: [],
        multiJoins: [], singleJoins: [],
    });

    it('route-encodes a single-PK joined id so special characters survive the route', () => {
        // The value becomes a route segment decoded by parsePkRoute; raw "/",
        // "%", "::", or spaces would build a broken or mis-split link.
        expect(getJoinedRowPkValue({ id: 'a/b c' }, tbl(['id']))).toBe('a%2Fb%20c');
        expect(getJoinedRowPkValue({ id: 'x::y' }, tbl(['id']))).toBe('x%3A%3Ay');
        expect(getJoinedRowPkValue({ id: '50%' }, tbl(['id']))).toBe('50%25');
    });

    it('leaves plain numeric ids unchanged (encoding is identity)', () => {
        expect(getJoinedRowPkValue({ id: 42 }, tbl(['id']))).toBe('42');
    });

    it('returns an empty string when the destination table is not in the schema', () => {
        // The `id` key is a GraphQL ALIAS the query builder emits for a single-PK
        // destination -- it only means anything once we know the destination table
        // and that it has one PK column. Without the schema there is no way to
        // know either, so reading `row.id` was a guess that produced a confident
        // link to /undefined/<whatever-happened-to-be-called-id>.
        expect(getJoinedRowPkValue({ id: 42 }, undefined)).toBe('');
    });

    it('returns an empty string for a missing row or null id', () => {
        expect(getJoinedRowPkValue(undefined, tbl(['id']))).toBe('');
        expect(getJoinedRowPkValue({ id: undefined }, tbl(['id']))).toBe('');
    });

    it('builds a composite route via rowIdOf for multi-column PK destinations', () => {
        const row = { region: 'eu/west', code: 7 };
        expect(getJoinedRowPkValue(row, tbl(['region', 'code']))).toBe('eu%2Fwest::7');
    });
});

describe('getSingleJoinRow', () => {
    it('reads an aliased single-join row from fieldName instead of destinationTable', () => {
        // orders carries two FKs to addresses, so the joined objects arrive under
        // billing_address / shipping_address and there is no `addresses` key at
        // all. Reading by destination table found nothing and the grid rendered
        // both address columns as NULL — worse than the raw id it used to show.
        const billing: Join = {
            name: 'addresses',
            fieldName: 'billing_address',
            sourceColumnNames: ['billing_address_id'],
            destinationTable: 'addresses',
            destinationColumnNames: ['address_id'],
        };
        const shipping: Join = {
            name: 'addresses',
            fieldName: 'shipping_address',
            sourceColumnNames: ['shipping_address_id'],
            destinationTable: 'addresses',
            destinationColumnNames: ['address_id'],
        };
        const row = {
            order_id: 2,
            billing_address: { id: 286, label: '8913 Redwood Way' },
            shipping_address: { id: 273, label: '7834 Oak Ave' },
        };

        expect(getSingleJoinRow(row, billing)).toEqual({ id: 286, label: '8913 Redwood Way' });
        expect(getSingleJoinRow(row, shipping)).toEqual({ id: 273, label: '7834 Oak Ave' });
    });

    it('falls back to destinationTable for a join with no alias', () => {
        const join: Join = {
            name: 'customers',
            sourceColumnNames: ['customer_id'],
            destinationTable: 'customers',
            destinationColumnNames: ['customer_id'],
        };
        const row = { order_id: 1, customers: { id: 7, label: 'Scott' } };

        expect(getSingleJoinRow(row, join)).toEqual({ id: 7, label: 'Scott' });
    });

    it('returns undefined when the FK is null', () => {
        const join: Join = {
            name: 'addresses',
            fieldName: 'billing_address',
            sourceColumnNames: ['billing_address_id'],
            destinationTable: 'addresses',
            destinationColumnNames: ['address_id'],
        };

        expect(getSingleJoinRow({ order_id: 1 }, join)).toBeUndefined();
    });
});
