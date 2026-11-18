import { describe, expect, it } from 'vitest';
import { buildGridGroupMemberRequest, buildGridGroupingRequest, groupingColumnFromUrl, groupingPage, groupingSumColumnFromUrl, GRID_GROUP_BY_PARAM, GRID_GROUP_SUM_PARAM, GROUPING_BUCKET_CAP, isSummableColumn, readGroupingResult, withGroupingUrlParam, withoutGroupingUrlParams, type GroupingSort } from './grid-grouping';
import type { Column, Table } from '../types/schema';

/** The server default `max-query-rows` (GqlObjectQuery.DefaultMaxQueryRows). */
const DEFAULT_CEILING = 10_000;

/**
 * Builds the grouping request against a server with `max-query-rows: ceiling`,
 * answers it the way that server does (the requested `limit` clamped to the
 * ceiling, as ResolveRowWindow clamps it, over these buckets in key order) and
 * reads the answer back.
 */
function grouped(
    buckets: readonly Record<string, unknown>[],
    { ceiling = DEFAULT_CEILING, table = orders, groupBy = orders.columns[0], sumBy = null, sort }: { ceiling?: number; table?: Table; groupBy?: Column; sumBy?: Column | null; sort?: GroupingSort } = {},
) {
    const request = buildGridGroupingRequest(table, groupBy, [], '', ceiling, sumBy, sort);
    const requested = Number(/limit: (\d+)\)/.exec(request.query)?.[1]);
    const data = { [`${table.name}Aggregate`]: buckets.slice(0, Math.min(requested, ceiling)) };
    return readGroupingResult(data, table, request);
}

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
        ], '', DEFAULT_CEILING);
        expect(request.query).toContain('ordersAggregate(filter: {status: {_null: true}}, groupBy: [status], limit: 1001)');
        expect(request.query).toContain('_count');
        expect(request.variables).toEqual({});
    });

    it('defines deterministic aggregate ordering by group key or server count direction', () => {
        const fixture = [
            { status: 'paid', _count: 2 },
            { status: 'cancelled', _count: 4 },
            { status: null, _count: 2 },
        ];
        expect(grouped(fixture, { sort: { field: 'key', desc: true } }).rows)
            .toEqual([{ value: 'paid', count: 2, sum: undefined }, { value: 'cancelled', count: 4, sum: undefined }, { value: null, count: 2, sum: undefined }]);
        // Equal counts have a key-ascending tie-breaker, so pagination cannot
        // shuffle aggregate buckets between requests.
        expect(grouped(fixture, { sort: { field: 'count', desc: true } }).rows)
            .toEqual([{ value: 'cancelled', count: 4, sum: undefined }, { value: null, count: 2, sum: undefined }, { value: 'paid', count: 2, sum: undefined }]);
        expect(buildGridGroupingRequest(orders, orders.columns[0], [], '', DEFAULT_CEILING, null, { field: 'count', desc: false }).sort)
            .toEqual({ field: 'count', desc: false });
    });

    it('uses the identical filter object for server count and sum aggregates, never a page-row total', () => {
        const request = buildGridGroupingRequest(orders, orders.columns[0], [
            { id: 'amount', value: { operator: '_gte', value: 10 } },
        ], JSON.stringify(['status', '_neq', 'cancelled', 'String']), DEFAULT_CEILING, groupingSumColumnFromUrl('amount', orders));
        expect(request.query).toContain('filter: {and: [{status: {_neq: $filter} }, {amount: {_gte: $cf_amount_0}}]}');
        expect(request.variables).toMatchObject({ filter: 'cancelled', cf_amount_0: '10' });
        expect(request.query).toContain('_count _sum { amount }');
        expect(request.query).toContain('ordersAggregate(filter: {and: [{status: {_neq: $filter} }, {amount: {_gte: $cf_amount_0}}]}, groupBy: [status], limit: 1001) {');
        // One aggregate field: no second GROUP BY probes the server's window.
        expect(request.query.match(/Aggregate\(/g)).toHaveLength(1);
        expect(request.query).not.toContain('offset');
    });

    it('matches fixture-backed SQL GROUP BY counts and configured sums without page-row arithmetic', () => {
        const sum = groupingSumColumnFromUrl('amount', orders);
        const request = buildGridGroupingRequest(orders, orders.columns[0], [], '', DEFAULT_CEILING, sum);
        expect(request.query).toContain('_sum { amount }');
        // Equivalent SQL fixture:
        // SELECT status, COUNT(*), SUM(amount) FROM orders GROUP BY status ORDER BY status.
        // These values represent the SQL-backed aggregate response, not a page.
        const aggregateFixture = [
            { status: null, _count: 1, _sum: { amount: '3.50' } },
            { status: 'cancelled', _count: 1, _sum: { amount: '2.00' } },
            { status: 'paid', _count: 2, _sum: { amount: '19.25' } },
        ];
        expect(grouped(aggregateFixture, { sumBy: sum })).toEqual({ rows: [
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
        expect(grouped([{ status: 'paid', _count: 42 }, { status: null, _count: 3 }]).rows)
            .toEqual([{ value: null, count: 3 }, { value: 'paid', count: 42 }]);
    });

    it('asks for one bucket past the cap and reports the cut instead of showing a partial set as whole', () => {
        const buckets = (n: number) => Array.from({ length: n }, (_, i) => ({ status: `s${String(i).padStart(5, '0')}`, _count: 1 }));
        const atCap = grouped(buckets(GROUPING_BUCKET_CAP));
        expect(atCap.truncated).toBe(false);
        expect(atCap.rows).toHaveLength(GROUPING_BUCKET_CAP);
        const over = grouped(buckets(GROUPING_BUCKET_CAP + 1));
        expect(over.truncated).toBe(true);
        expect(over.rows).toHaveLength(GROUPING_BUCKET_CAP);
        expect(over.rows.map((row) => row.value)).not.toContain(`s${String(GROUPING_BUCKET_CAP).padStart(5, '0')}`);
        const request = buildGridGroupingRequest(orders, orders.columns[0], [], '', DEFAULT_CEILING);
        expect(request.query).toContain(`groupBy: [status], limit: ${GROUPING_BUCKET_CAP + 1})`);
    });

    describe('under an operator max-query-rows below the cap', () => {
        const groups = (n: number) => Array.from({ length: n }, (_, i) => ({ status: `s${String(i).padStart(5, '0')}`, _count: 1 }));

        it('sizes the request from the server ceiling so the sentinel fits under it', () => {
            expect(groupingPage(500)).toBe(499);
            expect(buildGridGroupingRequest(orders, orders.columns[0], [], '', 500).query).toContain('limit: 500)');
            expect(groupingPage(2)).toBe(1);
            expect(groupingPage(DEFAULT_CEILING)).toBe(GROUPING_BUCKET_CAP);
        });

        it('reports the cut when the server ceiling, not the cap, ended the answer', () => {
            // max-query-rows 500, 700 groups: the page is 499 and the 500th bucket is the sentinel.
            const result = grouped(groups(700), { ceiling: 500 });
            expect(result.truncated).toBe(true);
            expect(result.rows).toHaveLength(499);
        });

        it('reports a complete answer that fits under the ceiling as complete', () => {
            expect(grouped(groups(300), { ceiling: 500 }).truncated).toBe(false);
            // Exactly the page: every bucket was returned and no sentinel came back.
            expect(grouped(groups(499), { ceiling: 500 })).toMatchObject({ truncated: false });
            expect(grouped(groups(499), { ceiling: 500 }).rows).toHaveLength(499);
            expect(grouped(groups(1), { ceiling: 2 }).truncated).toBe(false);
            expect(grouped([], { ceiling: 500 })).toEqual({ rows: [], truncated: false });
        });

        it('reports the cut one bucket past the page', () => {
            const result = grouped(groups(500), { ceiling: 500 });
            expect(result.truncated).toBe(true);
            expect(result.rows).toHaveLength(499);
        });

        it('reports the cut at the smallest ceiling the server accepts', () => {
            // RowProbeWindow.MinimumCeiling is 2: a page of one bucket plus the sentinel.
            const result = grouped(groups(2), { ceiling: 2 });
            expect(result.truncated).toBe(true);
            expect(result.rows).toHaveLength(1);
        });

        it('refuses to build a request without a usable server ceiling instead of guessing completeness', () => {
            expect(() => buildGridGroupingRequest(orders, orders.columns[0], [], '', undefined)).toThrow(/_maxQueryRows/);
            expect(() => groupingPage(1)).toThrow(/_maxQueryRows/);
            expect(() => groupingPage(2.5)).toThrow(/_maxQueryRows/);
        });
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
        ], '', DEFAULT_CEILING);
        expect(request.query).toContain('{at: {_between: [$cf_at_0_lo, $cf_at_0_hi]}}');
        expect(request.variables).toEqual({ cf_at_0_lo: '2024-03-01T00:00:00.000', cf_at_0_hi: '2024-03-01T23:59:59.999' });
    });

    it('sends a drill value that arrived as text as the column scalar', () => {
        const request = buildGridGroupingRequest(events, events.columns[0], [
            { id: 'level', value: { operator: '_eq', value: '5' } },
        ], '', DEFAULT_CEILING);
        expect(request.variables).toEqual({ cf_level_0: 5 });
        expect(buildGridGroupMemberRequest(events, events.columns[2], '5', [], '').variables.member).toBe(5);
    });

    it('refuses a filter the grid would refuse instead of counting a wider row set', () => {
        expect(() => buildGridGroupingRequest(events, events.columns[0], [], '["kind", "_eq", "x"]', DEFAULT_CEILING)).toThrow(/can't be applied/);
        expect(() => buildGridGroupingRequest(events, events.columns[0], [
            { id: 'level', value: { operator: '_contains', value: '5' } },
        ], '', DEFAULT_CEILING)).toThrow(/can't be applied/);
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
        ], '', DEFAULT_CEILING);

        expect(unknownTypeErrors(request.query, ORDERS_SDL)).toEqual([]);
        expect(declaredVariableTypes(request.query)).toEqual([
            { variable: 'cf_status_0', type: 'String' },
            { variable: 'cf_amount_1', type: 'Decimal' },
        ]);
    });

    it('declares no variables at all when the aggregate carries no filter', () => {
        const request = buildGridGroupingRequest(orders, orders.columns[0], [], '', DEFAULT_CEILING);

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
        const buckets = [
            { account: '10', _count: 1 },
            { account: '9', _count: 1 },
            { account: '-3', _count: 1 },
            { account: '9007199254740993', _count: 1 },
            { account: '9007199254740992', _count: 1 },
        ];
        expect(grouped(buckets, { table: ledger, groupBy: ledger.columns[0], sort: { field: 'key', desc: false } }).rows.map((r) => r.value))
            .toEqual(['-3', '9', '10', '9007199254740992', '9007199254740993']);
    });

    it('orders Decimal keys by value, not as text', () => {
        const prices = { name: 'prices', graphQlName: 'prices', columns: [
            { name: 'price', graphQlName: 'price', label: 'Price', paramType: 'Decimal' },
        ] } as unknown as Table;
        const buckets = [
            { price: '10.5', _count: 1 },
            { price: '9.25', _count: 1 },
            { price: '-3.75', _count: 1 },
            { price: '123456789012345678.0123456789', _count: 1 },
            { price: '123456789012345678.0123456788', _count: 1 },
        ];
        expect(grouped(buckets, { table: prices, groupBy: prices.columns[0], sort: { field: 'key', desc: false } }).rows.map((r) => r.value))
            .toEqual(['-3.75', '9.25', '10.5', '123456789012345678.0123456788', '123456789012345678.0123456789']);
    });

    it('keeps text order for a text column of digit strings', () => {
        expect(grouped([{ status: '10', _count: 1 }, { status: '9', _count: 1 }], { sort: { field: 'key', desc: false } }).rows.map((r) => r.value))
            .toEqual(['10', '9']);
    });
});
