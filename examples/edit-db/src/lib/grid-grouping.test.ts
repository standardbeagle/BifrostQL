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

// ── schema-derived variable declarations ─────────────────────────────────
// The server generates one filter input per table named TableFilter<name>Input
// (DbTable.TableFilterTypeName, src/BifrostQL.Core/Model/DbTable.cs:45) and one
// sort enum named <name>SortEnum (TableColumnSortEnumName, :46). A guessed
// "<name>Filter" parses fine but fails server-side validation with
// "Unknown type 'ordersFilter'.", so a text assertion on the built document is
// not enough — every declared variable type is checked against an SDL fixture
// shaped by those same generator rules.

/** Types the generator emits for the `orders` fixture, plus the built-in scalars. */
const ORDERS_SDL = `
    input TableFilterordersInput { status: FilterTypestringInput amount: FilterTypedecimalInput and: [TableFilterordersInput] or: [TableFilterordersInput] }
    input FilterTypestringInput { _eq: String _neq: String _contains: String _null: Boolean }
    input FilterTypedecimalInput { _eq: Decimal _gte: Decimal _lte: Decimal _null: Boolean }
    enum ordersSortEnum { status_asc status_desc amount_asc amount_desc }
    enum ordersColumn { status amount }
    scalar Decimal
`;

/** Every named type the SDL fixture defines, plus the GraphQL built-in scalars. */
function sdlTypeNames(sdl: string): Set<string> {
    const declared = [...sdl.matchAll(/^\s*(?:input|enum|type|scalar|union|interface)\s+([_A-Za-z][_0-9A-Za-z]*)/gm)].map((match) => match[1]);
    return new Set([...declared, 'Int', 'Float', 'String', 'Boolean', 'ID']);
}

/** Variable declarations of a built document, as `$name: Type` pairs with list/non-null wrappers stripped. */
function declaredVariableTypes(query: string): { variable: string; type: string }[] {
    const header = /query\s+[_A-Za-z][_0-9A-Za-z]*\s*\(([^)]*)\)/.exec(query);
    if (!header) return [];
    return [...header[1].matchAll(/\$([_A-Za-z][_0-9A-Za-z]*)\s*:\s*([[\]!_0-9A-Za-z]+)/g)]
        .map((match) => ({ variable: match[1], type: match[2].replace(/[[\]!]/g, '') }));
}

/** The validation error the server raises for a declared type the schema does not define. */
function unknownTypeErrors(query: string, sdl: string): string[] {
    const known = sdlTypeNames(sdl);
    return declaredVariableTypes(query)
        .filter((declaration) => !known.has(declaration.type))
        .map((declaration) => `Unknown type '${declaration.type}'.`);
}

describe('grid grouping documents validate against the generated schema', () => {
    it('declares the generated TableFilter<name>Input for the filtered aggregate', () => {
        const request = buildGridGroupingRequest(orders, orders.columns[0], [
            { id: 'status', value: { operator: '_eq', value: 'paid' } },
        ], '');

        expect(unknownTypeErrors(request.query, ORDERS_SDL)).toEqual([]);
        expect(request.query).toContain('($filter: TableFilterordersInput)');
        expect(request.query).not.toContain('ordersFilter');
    });

    it('declares no variables at all when the aggregate carries no filter', () => {
        const request = buildGridGroupingRequest(orders, orders.columns[0], [], '');

        expect(declaredVariableTypes(request.query)).toEqual([]);
        expect(unknownTypeErrors(request.query, ORDERS_SDL)).toEqual([]);
    });

    it('declares generated type names for every group-member variable', () => {
        const request = buildGridGroupMemberRequest(orders, orders.columns[0], 'paid', [], '', ['amount_desc']);

        expect(declaredVariableTypes(request.query)).toEqual([
            { variable: 'filter', type: 'TableFilterordersInput' },
            { variable: 'sort', type: 'ordersSortEnum' },
            { variable: 'limit', type: 'Int' },
            { variable: 'offset', type: 'Int' },
        ]);
        expect(unknownTypeErrors(request.query, ORDERS_SDL)).toEqual([]);
    });

    it('reports the server-side unknown-type error when a document names an undefined input', () => {
        // Guards the checker itself: the pre-fix "<name>Filter" spelling must be
        // reported, otherwise the two assertions above would be vacuous.
        const preFix = 'query GridGroupMembers($filter: ordersFilter, $limit: Int) { orders { total } }';

        expect(unknownTypeErrors(preFix, ORDERS_SDL)).toEqual(["Unknown type 'ordersFilter'."]);
    });
});
