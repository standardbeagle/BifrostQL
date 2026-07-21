import { describe, expect, it } from 'vitest';
import { buildGridGroupingRequest, groupingColumnFromUrl, readGroupingRows } from './grid-grouping';
import type { Table } from '../types/schema';

const orders = {
    name: 'orders', graphQlName: 'orders', columns: [
        { name: 'status', graphQlName: 'status', label: 'Status', paramType: 'String' },
        { name: 'amount', graphQlName: 'amount', label: 'Amount', paramType: 'Decimal' },
    ],
} as unknown as Table;

describe('grid grouping request', () => {
    it('resolves only a column from the active schema', () => {
        expect(groupingColumnFromUrl('status', orders)?.name).toBe('status');
        expect(groupingColumnFromUrl('status) { bad', orders)).toBeNull();
    });

    it('uses the server aggregate and preserves null filters as boolean predicates', () => {
        const request = buildGridGroupingRequest(orders, orders.columns[0], [
            { id: 'status', value: { operator: '_null', value: true } },
        ], '');
        expect(request.query).toContain('ordersAggregate(filter: $filter, groupBy: [status])');
        expect(request.query).toContain('_count');
        expect(request.query).not.toContain('limit:');
        expect(request.variables).toEqual({ filter: { status: { _null: true } } });
    });

    it('maps aggregate result rows rather than summing page rows', () => {
        expect(readGroupingRows({ ordersAggregate: [{ status: 'paid', _count: 42 }, { status: null, _count: 3 }] }, orders, orders.columns[0]))
            .toEqual([{ value: 'paid', count: 42 }, { value: null, count: 3 }]);
    });
});
