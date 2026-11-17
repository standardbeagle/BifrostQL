import { describe, expect, it } from 'vitest';
import { buildGridGroupMemberRequest, buildGridGroupingRequest, groupingColumnFromUrl, groupingSumColumnFromUrl, GRID_GROUP_BY_PARAM, GRID_GROUP_SUM_PARAM, GROUPING_BUCKET_CAP, isSummableColumn, readGroupingResult, withGroupingUrlParam, withoutGroupingUrlParams } from './grid-grouping';
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
        expect(request.query).toContain('ordersAggregate(filter: {status: {_null: true}}, groupBy: [status], limit: 1001)');
        expect(request.query).toContain('_count');
        expect(request.variables).toEqual({});
    });

    it('defines deterministic aggregate ordering by group key or server count direction', () => {
        const fixture = { ordersAggregate: [
            { status: 'paid', _count: 2 },
            { status: 'cancelled', _count: 4 },
            { status: null, _count: 2 },
        ] };
        expect(readGroupingResult(fixture, orders, orders.columns[0], null, { field: 'key', desc: true }).rows)
            .toEqual([{ value: 'paid', count: 2, sum: undefined }, { value: 'cancelled', count: 4, sum: undefined }, { value: null, count: 2, sum: undefined }]);
        // Equal counts have a key-ascending tie-breaker, so pagination cannot
        // shuffle aggregate buckets between requests.
        expect(readGroupingResult(fixture, orders, orders.columns[0], null, { field: 'count', desc: true }).rows)
            .toEqual([{ value: 'cancelled', count: 4, sum: undefined }, { value: null, count: 2, sum: undefined }, { value: 'paid', count: 2, sum: undefined }]);
        expect(buildGridGroupingRequest(orders, orders.columns[0], [], '', null, { field: 'count', desc: false }).sort)
            .toEqual({ field: 'count', desc: false });
    });

    it('uses the identical filter object for server count and sum aggregates, never a page-row total', () => {
        const request = buildGridGroupingRequest(orders, orders.columns[0], [
            { id: 'amount', value: { operator: '_gte', value: 10 } },
        ], JSON.stringify(['status', '_neq', 'cancelled', 'String']), groupingSumColumnFromUrl('amount', orders));
        expect(request.query).toContain('filter: {and: [{status: {_neq: $filter} }, {amount: {_gte: $cf_amount_0}}]}');
        expect(request.variables).toMatchObject({ filter: 'cancelled', cf_amount_0: '10' });
        expect(request.query).toContain('_count _sum { amount }');
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
        expect(readGroupingResult(aggregateFixture, orders, orders.columns[0], sum)).toEqual({ rows: [
            { value: null, count: 1, sum: '3.50' },
            { value: 'cancelled', count: 1, sum: '2.00' },
            { value: 'paid', count: 2, sum: '19.25' },
        ], truncated: false });
        expect(groupingSumColumnFromUrl('status', orders)).toBeNull();
    });

    it('expands only the selected group and merges active filters, including distinct null and empty-string semantics', () => {
        const nullMembers = buildGridGroupMemberRequest(orders, orders.columns[0], null, [{ id: 'amount', value: { operator: '_gte', value: 10 } }], '', ['amount_desc']);
        expect(nullMembers.query).toContain('$sort: [ordersSortEnum!]');
        expect(nullMembers.query).toContain('orders(filter: {and: [{amount: {_gte: $cf_amount_0}}, {status: {_null: true}}]} sort: $sort limit: $limit offset: $offset)');
        expect(nullMembers.query).not.toContain('$member');
        expect(nullMembers.variables).toMatchObject({ cf_amount_0: '10', sort: ['amount_desc'] });
        expect(nullMembers.responseKey).toBe('orders');
        const emptyMembers = buildGridGroupMemberRequest(orders, orders.columns[0], '', [], '');
        expect(emptyMembers.query).toContain('orders(filter: {status: {_eq: $member}}');
        expect(emptyMembers.variables.member).toBe('');
    });

    it('maps aggregate result rows rather than summing page rows and defines key-ascending group order', () => {
        expect(readGroupingResult({ ordersAggregate: [{ status: 'paid', _count: 42 }, { status: null, _count: 3 }] }, orders, orders.columns[0], null).rows)
            .toEqual([{ value: null, count: 3 }, { value: 'paid', count: 42 }]);
    });

    it('asks for one bucket past the cap and reports the cut instead of showing a partial set as whole', () => {
        const buckets = (n: number) => ({ ordersAggregate: Array.from({ length: n }, (_, i) => ({ status: `s${String(i).padStart(5, '0')}`, _count: 1 })) });
        const atCap = readGroupingResult(buckets(GROUPING_BUCKET_CAP), orders, orders.columns[0], null);
        expect(atCap.truncated).toBe(false);
        expect(atCap.rows).toHaveLength(GROUPING_BUCKET_CAP);
        const over = readGroupingResult(buckets(GROUPING_BUCKET_CAP + 1), orders, orders.columns[0], null);
        expect(over.truncated).toBe(true);
        expect(over.rows).toHaveLength(GROUPING_BUCKET_CAP);
        expect(over.rows.map((row) => row.value)).not.toContain(`s${String(GROUPING_BUCKET_CAP).padStart(5, '0')}`);
        const request = buildGridGroupingRequest(orders, orders.columns[0], [], '');
        expect(request.query).toContain(`groupBy: [status], limit: ${GROUPING_BUCKET_CAP + 1})`);
    });
});

describe('grid grouping filters match the grid query', () => {
    const events = {
        name: 'events', graphQlName: 'events', columns: [
            { name: 'kind', graphQlName: 'kind', label: 'Kind', paramType: 'String' },
            { name: 'at', graphQlName: 'at', label: 'At', paramType: 'DateTime' },
            { name: 'level', graphQlName: 'level', label: 'Level', paramType: 'Int' },
        ],
    } as unknown as Table;

    it('widens a date-only "on" filter to the same day range the grid uses', () => {
        const request = buildGridGroupingRequest(events, events.columns[0], [
            { id: 'at', value: { operator: '_eq', value: '2024-03-01' } },
        ], '');
        expect(request.query).toContain('{at: {_between: [$cf_at_0_lo, $cf_at_0_hi]}}');
        expect(request.variables).toEqual({ cf_at_0_lo: '2024-03-01T00:00:00.000', cf_at_0_hi: '2024-03-01T23:59:59.999' });
    });

    it('sends a drill value that arrived as text as the column scalar', () => {
        const request = buildGridGroupingRequest(events, events.columns[0], [
            { id: 'level', value: { operator: '_eq', value: '5' } },
        ], '');
        expect(request.variables).toEqual({ cf_level_0: 5 });
        expect(buildGridGroupMemberRequest(events, events.columns[2], '5', [], '').variables.member).toBe(5);
    });

    it('refuses a filter the grid would refuse instead of counting a wider row set', () => {
        expect(() => buildGridGroupingRequest(events, events.columns[0], [], '["kind", "_eq", "x"]')).toThrow(/can't be applied/);
        expect(() => buildGridGroupingRequest(events, events.columns[0], [
            { id: 'level', value: { operator: '_contains', value: '5' } },
        ], '')).toThrow(/can't be applied/);
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
    it('declares each filter variable as its column scalar for the filtered aggregate', () => {
        const request = buildGridGroupingRequest(orders, orders.columns[0], [
            { id: 'status', value: { operator: '_eq', value: 'paid' } },
            { id: 'amount', value: { operator: '_gte', value: '10' } },
        ], '');

        expect(unknownTypeErrors(request.query, ORDERS_SDL)).toEqual([]);
        expect(declaredVariableTypes(request.query)).toEqual([
            { variable: 'cf_status_0', type: 'String' },
            { variable: 'cf_amount_1', type: 'Decimal' },
        ]);
    });

    it('declares no variables at all when the aggregate carries no filter', () => {
        const request = buildGridGroupingRequest(orders, orders.columns[0], [], '');

        expect(declaredVariableTypes(request.query)).toEqual([]);
        expect(unknownTypeErrors(request.query, ORDERS_SDL)).toEqual([]);
    });

    it('declares generated type names for every group-member variable', () => {
        const request = buildGridGroupMemberRequest(orders, orders.columns[0], 'paid', [], '', ['amount_desc']);

        expect(declaredVariableTypes(request.query)).toEqual([
            { variable: 'member', type: 'String' },
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

describe('grouping sum columns', () => {
    const ledger = {
        name: 'ledger', graphQlName: 'ledger', columns: [
            { name: 'total', graphQlName: 'total', label: 'Total', paramType: 'BigInt!' },
            { name: 'small', graphQlName: 'small', label: 'Small', paramType: 'Short' },
            { name: 'rate', graphQlName: 'rate', label: 'Rate', paramType: 'Decimal' },
            { name: 'memo', graphQlName: 'memo', label: 'Memo', paramType: 'String' },
            { name: 'at', graphQlName: 'at', label: 'At', paramType: 'DateTime' },
        ],
    } as unknown as Table;

    it('offers every numeric scalar, BigInt included, and nothing else', () => {
        expect(ledger.columns.filter(isSummableColumn).map((c) => c.name)).toEqual(['total', 'small', 'rate']);
        expect(groupingSumColumnFromUrl('total', ledger)?.name).toBe('total');
        expect(groupingSumColumnFromUrl('memo', ledger)).toBeNull();
    });
});

describe('grouping keys sent as BigInt strings', () => {
    it('orders BigInt keys numerically, not as text', () => {
        const ledger = { name: 'ledger', graphQlName: 'ledger', columns: [
            { name: 'account', graphQlName: 'account', label: 'Account', paramType: 'BigInt' },
        ] } as unknown as Table;
        const data = { ledgerAggregate: [
            { account: '10', _count: 1 },
            { account: '9', _count: 1 },
            { account: '-3', _count: 1 },
            { account: '9007199254740993', _count: 1 },
            { account: '9007199254740992', _count: 1 },
        ] };
        expect(readGroupingResult(data, ledger, ledger.columns[0], null, { field: 'key', desc: false }).rows.map((r) => r.value))
            .toEqual(['-3', '9', '10', '9007199254740992', '9007199254740993']);
    });

    it('keeps text order for a text column of digit strings', () => {
        const data = { ordersAggregate: [{ status: '10', _count: 1 }, { status: '9', _count: 1 }] };
        expect(readGroupingResult(data, orders, orders.columns[0], null, { field: 'key', desc: false }).rows.map((r) => r.value))
            .toEqual(['10', '9']);
    });
});
