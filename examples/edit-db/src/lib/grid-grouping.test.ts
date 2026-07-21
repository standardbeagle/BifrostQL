import { describe, expect, it } from 'vitest';
import { buildGridGroupMemberRequest, buildGridGroupingRequest, groupingColumnFromUrl, groupingSumColumnFromUrl, readGroupingRows, readGroupingRowsWithSum } from './grid-grouping';
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

    it('requests one schema-configured server sum and maps its server value unchanged', () => {
        const sum = groupingSumColumnFromUrl('amount', orders);
        const request = buildGridGroupingRequest(orders, orders.columns[0], [], '', sum);
        expect(request.query).toContain('_sum { amount }');
        // This fixture is the aggregate result from the SQL-backed server; no page-row arithmetic is involved.
        expect(readGroupingRowsWithSum({ ordersAggregate: [{ status: 'paid', _count: 2, _sum: { amount: '19.25' } }] }, orders, orders.columns[0], sum))
            .toMatchObject([{ value: 'paid', count: 2, sum: '19.25' }]);
        expect(groupingSumColumnFromUrl('status', orders)).toBeNull();
    });

    it('expands only the selected group and merges active filters, including distinct null and empty-string semantics', () => {
        const nullMembers = buildGridGroupMemberRequest(orders, orders.columns[0], null, [{ id: 'amount', value: { operator: '_gte', value: 10 } }], '');
        expect(nullMembers.query).toContain('orders(filter: $filter limit: $limit offset: $offset)');
        expect(nullMembers.variables.filter).toEqual({ and: [{ amount: { _gte: 10 } }, { status: { _null: true } }] });
        expect(buildGridGroupMemberRequest(orders, orders.columns[0], '', [], '').variables.filter).toEqual({ status: { _eq: '' } });
    });

    it('maps aggregate result rows rather than summing page rows', () => {
        expect(readGroupingRows({ ordersAggregate: [{ status: 'paid', _count: 42 }, { status: null, _count: 3 }] }, orders, orders.columns[0]))
            .toEqual([{ value: 'paid', count: 42 }, { value: null, count: 3 }]);
    });
});
