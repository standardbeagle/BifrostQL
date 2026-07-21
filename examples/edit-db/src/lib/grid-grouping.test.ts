import { describe, expect, it } from 'vitest';
import { buildGridGroupMemberRequest, buildGridGroupingRequest, groupingColumnFromUrl, groupingSumColumnFromUrl, GRID_GROUP_BY_PARAM, GRID_GROUP_SUM_PARAM, readGroupingRows, readGroupingRowsWithSum, withGroupingUrlParam, withoutGroupingUrlParams } from './grid-grouping';
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

    it('round-trips URL-owned grouping state without dropping unrelated parameters', () => {
        const initial = new URLSearchParams('cf=active&profile=readonly');
        const grouped = withGroupingUrlParam(initial, GRID_GROUP_BY_PARAM, 'status');
        const withMeasure = withGroupingUrlParam(grouped, GRID_GROUP_SUM_PARAM, 'amount');

        expect(withMeasure.toString()).toBe('cf=active&profile=readonly&gb=status&gs=amount');
        expect(groupingColumnFromUrl(withMeasure.get(GRID_GROUP_BY_PARAM), orders)?.name).toBe('status');
        expect(groupingSumColumnFromUrl(withMeasure.get(GRID_GROUP_SUM_PARAM), orders)?.name).toBe('amount');
        expect(withGroupingUrlParam(withMeasure, GRID_GROUP_BY_PARAM, null).get(GRID_GROUP_BY_PARAM)).toBeNull();
    });

    it('clears gb and gs on a table switch while retaining unrelated URL state', () => {
        const oldTableUrl = new URLSearchParams('cf=active&gb=status&gs=amount&profile=readonly');
        const switched = withoutGroupingUrlParams(oldTableUrl);

        expect(switched.toString()).toBe('cf=active&profile=readonly');
        // A shared column name on the next table must not revive old grouping.
        expect(groupingColumnFromUrl(switched.get(GRID_GROUP_BY_PARAM), orders)).toBeNull();
        expect(groupingSumColumnFromUrl(switched.get(GRID_GROUP_SUM_PARAM), orders)).toBeNull();
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

    it('defines deterministic aggregate ordering by group key or server count direction', () => {
        const fixture = { ordersAggregate: [
            { status: 'paid', _count: 2 },
            { status: 'cancelled', _count: 4 },
            { status: null, _count: 2 },
        ] };
        expect(readGroupingRows(fixture, orders, orders.columns[0], { field: 'key', desc: true }))
            .toEqual([{ value: 'paid', count: 2, sum: undefined }, { value: 'cancelled', count: 4, sum: undefined }, { value: null, count: 2, sum: undefined }]);
        // Equal counts have a key-ascending tie-breaker, so pagination cannot
        // shuffle aggregate buckets between requests.
        expect(readGroupingRows(fixture, orders, orders.columns[0], { field: 'count', desc: true }))
            .toEqual([{ value: 'cancelled', count: 4, sum: undefined }, { value: null, count: 2, sum: undefined }, { value: 'paid', count: 2, sum: undefined }]);
        expect(buildGridGroupingRequest(orders, orders.columns[0], [], '', null, { field: 'count', desc: false }).sort)
            .toEqual({ field: 'count', desc: false });
    });

    it('uses the identical filter object for server count and sum aggregates, never a page-row total', () => {
        const request = buildGridGroupingRequest(orders, orders.columns[0], [
            { id: 'amount', value: { operator: '_gte', value: 10 } },
        ], JSON.stringify(['status', '_neq', 'cancelled', 'String']), groupingSumColumnFromUrl('amount', orders));
        expect(request.variables).toEqual({
            filter: { and: [{ status: { _neq: 'cancelled' } }, { amount: { _gte: 10 } }] },
        });
        expect(request.query).toContain('_count _sum { amount }');
        expect(request.query).not.toContain('limit:');
        expect(request.query).not.toContain('offset:');
    });

    it('matches fixture-backed SQL GROUP BY counts and configured sums without page-row arithmetic', () => {
        const sum = groupingSumColumnFromUrl('amount', orders);
        const request = buildGridGroupingRequest(orders, orders.columns[0], [], '', sum);
        expect(request.query).toContain('_sum { amount }');
        // Equivalent SQL fixture:
        // SELECT status, COUNT(*), SUM(amount) FROM orders GROUP BY status ORDER BY status.
        // These values represent the SQL-backed aggregate response, not a page.
        const aggregateFixture = {
            ordersAggregate: [
                { status: null, _count: 1, _sum: { amount: '3.50' } },
                { status: 'cancelled', _count: 1, _sum: { amount: '2.00' } },
                { status: 'paid', _count: 2, _sum: { amount: '19.25' } },
            ],
        };
        expect(readGroupingRowsWithSum(aggregateFixture, orders, orders.columns[0], sum)).toEqual([
            { value: null, count: 1, sum: '3.50' },
            { value: 'cancelled', count: 1, sum: '2.00' },
            { value: 'paid', count: 2, sum: '19.25' },
        ]);
        expect(groupingSumColumnFromUrl('status', orders)).toBeNull();
    });

    it('expands only the selected group and merges active filters, including distinct null and empty-string semantics', () => {
        const nullMembers = buildGridGroupMemberRequest(orders, orders.columns[0], null, [{ id: 'amount', value: { operator: '_gte', value: 10 } }], '', ['amount_desc']);
        expect(nullMembers.query).toContain('$sort: [ordersSortEnum!]');
        expect(nullMembers.query).toContain('orders(filter: $filter sort: $sort limit: $limit offset: $offset)');
        expect(nullMembers.variables.filter).toEqual({ and: [{ amount: { _gte: 10 } }, { status: { _null: true } }] });
        expect(nullMembers.variables.sort).toEqual(['amount_desc']);
        expect(nullMembers.responseKey).toBe('orders');
        expect(buildGridGroupMemberRequest(orders, orders.columns[0], '', [], '').variables.filter).toEqual({ status: { _eq: '' } });
    });

    it('maps aggregate result rows rather than summing page rows and defines key-ascending group order', () => {
        expect(readGroupingRows({ ordersAggregate: [{ status: 'paid', _count: 42 }, { status: null, _count: 3 }] }, orders, orders.columns[0]))
            .toEqual([{ value: null, count: 3 }, { value: 'paid', count: 42 }]);
    });
});
