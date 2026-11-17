import { describe, expect, it } from 'vitest';
import { render } from '@testing-library/react';
import type { ReactNode } from 'react';
import { getMultiJoinRows, getSingleJoinRow, clampPageIndex, getJoinedRowPkValue, reconcileColumnFiltersFromUrl, resolveConfiguredSort, getTableColumns } from './useDataTable';
import type { SchemaContextValue } from '../types/schema';
import { serializeColumnFilters } from '../lib/query-builder';
import { encodeRouteParts } from '../lib/row-id';
import type { ColumnFiltersState } from '@tanstack/react-table';
import type { Column, Join, Table } from '../types/schema';

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

    it('returns the raw single-PK key so the FK popover `_eq` filter matches', () => {
        // FkCellPopover uses this value as the `_eq` variable. A String FK of
        // "US/CA" (or one with a space / "%") route-encoded to "US%2FCA" never
        // matches a stored row, so the popover shows "Record not found".
        expect(getJoinedRowPkValue({ id: 'US/CA' }, tbl(['id']))).toBe('US/CA');
        expect(getJoinedRowPkValue({ id: 'a b' }, tbl(['id']))).toBe('a b');
        expect(getJoinedRowPkValue({ id: '50%' }, tbl(['id']))).toBe('50%');
    });

    it('route-encodes the raw key at the route boundary only', () => {
        // The Link route segment must still carry the encoded form, decoded by
        // parsePkRoute; encodeRouteParts is the one producer of that encoding.
        expect(encodeRouteParts([getJoinedRowPkValue({ id: 'US/CA' }, tbl(['id']))])).toBe('US%2FCA');
        expect(encodeRouteParts([getJoinedRowPkValue({ id: 'x::y' }, tbl(['id']))])).toBe('x%3A%3Ay');
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

describe('resolveConfiguredSort', () => {
    const col = (name: string, dbType = 'int'): Column => ({
        dbName: name, graphQlName: name, name, label: name, paramType: 'Int',
        dbType, isPrimaryKey: name === 'id', isIdentity: false, isNullable: false,
        isReadOnly: false, metadata: {},
    });
    const table = (name: string, columns: Column[]): Table => ({
        dbName: name, graphQlName: name, name, label: name, labelColumn: 'name',
        primaryKeys: ['id'], isEditable: true, metadata: {}, columns,
        multiJoins: [], singleJoins: [],
    });
    const workshops = table('workshops', [col('id'), col('created', 'datetime'), col('notes', 'varbinary')]);

    it('opens every table newest-first from a "*" entry', () => {
        expect(resolveConfiguredSort(workshops, { '*': { column: 'id', desc: true } }))
            .toEqual([{ id: 'id', desc: true }]);
    });

    it('lets a table-specific entry win over "*"', () => {
        expect(resolveConfiguredSort(workshops, {
            '*': { column: 'id', desc: true },
            workshops: { column: 'created', desc: true },
        })).toEqual([{ id: 'created', desc: true }]);
    });

    it('defaults the direction to ascending', () => {
        expect(resolveConfiguredSort(workshops, { workshops: { column: 'created' } }))
            .toEqual([{ id: 'created', desc: false }]);
    });

    it('ignores an entry naming a column the table does not have', () => {
        // A host-wide `created desc` must not error the first query of a table
        // without that column; the hook falls back to its positional default.
        expect(resolveConfiguredSort(workshops, { '*': { column: 'modified', desc: true } })).toEqual([]);
    });

    it('ignores a column that cannot drive an ORDER BY', () => {
        expect(resolveConfiguredSort(workshops, { workshops: { column: 'notes' } })).toEqual([]);
    });

    it('is empty with no table or no config', () => {
        expect(resolveConfiguredSort(null, { '*': { column: 'id' } })).toEqual([]);
        expect(resolveConfiguredSort(workshops, undefined)).toEqual([]);
    });
});

describe('getTableColumns: type-based cell rendering', () => {
    const col = (name: string, paramType: string, metadata: Column['metadata'] = {}): Column => ({
        dbName: name, graphQlName: name, name, label: name, paramType,
        dbType: paramType.startsWith('Boolean') ? 'bit' : 'datetime2', isPrimaryKey: false, isIdentity: false,
        isNullable: !paramType.endsWith('!'), isReadOnly: false, metadata,
    });
    const relative = { 'display-format': 'relative' };
    const table: Table = {
        dbName: 'events', graphQlName: 'events', name: 'events', label: 'events', labelColumn: 'id',
        primaryKeys: ['id'], isEditable: true, metadata: {},
        columns: [col('maybeAt', 'DateTime', relative), col('at', 'DateTime!', relative), col('done', 'Boolean')],
        multiJoins: [], singleJoins: [],
    };
    // Scalar columns never consult the schema; a join column would.
    const columns = getTableColumns(table, {} as SchemaContextValue);
    const def = (id: string) => columns.find((c) => c.id === id)! as unknown as {
        cell: (ctx: { row: { original: Record<string, unknown>; index: number } }) => ReactNode;
        accessorFn: (row: Record<string, unknown>) => unknown;
    };
    const cellText = (id: string, value: unknown) =>
        render(<>{def(id).cell({ row: { original: { [id]: value }, index: 0 } })}</>).container;

    it('honors display-format on a nullable DateTime column exactly as on a NOT NULL one', () => {
        const threeHoursAgo = new Date(Date.now() - 3 * 3600_000).toISOString();
        expect(cellText('maybeAt', threeHoursAgo).textContent).toBe('3 hours ago');
        expect(cellText('at', threeHoursAgo).textContent).toBe('3 hours ago');
    });

    it('sorts and filters a nullable DateTime on its stored value, not localized text', () => {
        expect(def('maybeAt').accessorFn({ maybeAt: '2024-06-15T10:30:00Z' })).toBe('2024-06-15T10:30:00Z');
    });

    it('shows a sentinel instant in a nullable DateTime as unset, not a blank cell', () => {
        const container = cellText('maybeAt', '0001-01-01T00:00:00');
        expect(container.textContent).toBe('unset');
        expect(container.querySelector('[title]')!.getAttribute('title')).toBe('0001-01-01T00:00:00');
    });

    it('renders a Boolean cell as a glyph that still reads as the value', () => {
        const container = cellText('done', false);
        expect(container.querySelector('svg')).not.toBeNull();
        expect(container.querySelector('.sr-only')!.textContent).toBe('false');
    });
});

describe('getTableColumns: FK filter column', () => {
    const col = (name: string, paramType = 'Int'): Column => ({
        dbName: name, graphQlName: name, name, label: name, paramType, dbType: 'int',
        isPrimaryKey: false, isIdentity: false, isNullable: true, isReadOnly: false, metadata: {},
    });
    const tenants: Table = {
        dbName: 'tenant_codes', graphQlName: 'tenant_codes', name: 'tenant_codes', label: 'Codes', labelColumn: 'title',
        primaryKeys: ['tenant_id', 'code'], isEditable: true, metadata: {},
        columns: [col('tenant_id'), col('code', 'String'), col('title', 'String')], multiJoins: [], singleJoins: [],
    };
    const owners: Table = {
        dbName: 'owners', graphQlName: 'owners', name: 'owners', label: 'Owners', labelColumn: 'name',
        primaryKeys: ['uid'], isEditable: true, metadata: {},
        columns: [col('uid'), col('name', 'String')], multiJoins: [], singleJoins: [],
    };
    const byCode: Join = { name: 'tenant_codes', sourceColumnNames: ['tenant_id', 'code'], destinationTable: 'tenant_codes', destinationColumnNames: ['tenant_id', 'code'] } as Join;
    const byOwner: Join = { name: 'owners', sourceColumnNames: ['owner_id'], destinationTable: 'owners', destinationColumnNames: ['uid'] } as Join;
    const items: Table = {
        dbName: 'items', graphQlName: 'items', name: 'items', label: 'Items', labelColumn: 'id',
        primaryKeys: ['id'], isEditable: true, metadata: {},
        columns: [col('id'), col('tenant_id'), col('code', 'String'), col('owner_id')],
        multiJoins: [], singleJoins: [byCode, byOwner],
    };
    const schema = { findTable: (name: string) => (name === 'tenant_codes' ? tenants : name === 'owners' ? owners : undefined) } as unknown as SchemaContextValue;
    const meta = (id: string) => (getTableColumns(items, schema).find((c) => c.id === id)!.meta as Record<string, unknown>);

    it('offers no FK dropdown on a composite FK, whose options would list one column of the tuple', () => {
        // The dropdown projected `id: tenant_id` and filtered on tenant_id alone.
        expect(meta('tenant_id').joinFkColumn).toBeUndefined();
        expect(meta('code').joinFkColumn).toBeUndefined();
        expect(meta('tenant_id').isCompositeFk).toBe(true);
    });

    it('gives a composite FK anchor every column pair for the tuple filter, and members none', () => {
        expect(meta('tenant_id').fkSourceColumns).toEqual(['tenant_id', 'code']);
        expect(meta('tenant_id').fkDestinationColumns).toEqual(['tenant_id', 'code']);
        expect(meta('code').fkSourceColumns).toBeUndefined();
        expect(meta('owner_id').fkSourceColumns).toBeUndefined();
    });

    it('points a single-column FK dropdown at the destination column it references', () => {
        expect(meta('owner_id').joinFkColumn).toBe('uid');
    });
});
