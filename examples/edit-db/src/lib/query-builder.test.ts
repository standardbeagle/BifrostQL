import { describe, it, expect } from 'vitest';
import {
    getFilterOperators,
    getFilterObj,
    toLocaleDate,
    getRowPkValue,
    getGraphQlType,
    buildColumnFilters,
    serializeColumnFilters,
    deserializeColumnFilters,
    getPkType,
    getPkTypes,
    buildPkEqVariables,
    buildQuery,
    type ColumnFilterValue,
} from './query-builder';
import type { Table, Schema, Column, Join } from '../types/schema';

// ── Test Fixtures ──────────────────────────────────────────────

function makeColumn(overrides: Partial<Column> = {}): Column {
    return {
        dbName: 'col',
        graphQlName: 'col',
        name: 'col',
        label: 'Col',
        paramType: 'String',
        dbType: 'nvarchar',
        isPrimaryKey: false,
        isIdentity: false,
        isNullable: true,
        isReadOnly: false,
        metadata: {},
        ...overrides,
    };
}

function makeTable(overrides: Partial<Table> = {}): Table {
    return {
        dbName: 'dbo.test',
        graphQlName: 'test',
        name: 'test',
        label: 'Test',
        labelColumn: 'name',
        primaryKeys: ['id'],
        isEditable: true,
        metadata: {},
        columns: [
            makeColumn({ name: 'id', paramType: 'Int!', isPrimaryKey: true, isIdentity: true }),
            makeColumn({ name: 'name', paramType: 'String!' }),
        ],
        multiJoins: [],
        singleJoins: [],
        ...overrides,
    };
}

function makeSchema(tables: Table[]): Schema {
    return {
        loading: false,
        error: null,
        data: tables,
        findTable: (name: string) => tables.find(t => t.name === name || t.graphQlName === name),
    };
}

// ── getFilterOperators ─────────────────────────────────────────

describe('getFilterOperators', () => {
    it('returns String operators for String type', () => {
        const ops = getFilterOperators('String');
        expect(ops).toContain('_contains');
        expect(ops).toContain('_starts_with');
        expect(ops).not.toContain('_gt');
    });

    it('returns Int operators for Int type', () => {
        const ops = getFilterOperators('Int');
        expect(ops).toContain('_gt');
        expect(ops).toContain('_between');
        expect(ops).not.toContain('_contains');
    });

    it('strips ! from non-null types', () => {
        expect(getFilterOperators('Int!')).toEqual(getFilterOperators('Int'));
        expect(getFilterOperators('String!')).toEqual(getFilterOperators('String'));
    });

    it('returns Boolean operators (only _eq and _null)', () => {
        const ops = getFilterOperators('Boolean');
        expect(ops).toEqual(['_eq', '_null']);
    });

    it('returns DateTime operators', () => {
        const ops = getFilterOperators('DateTime');
        expect(ops).toContain('_between');
        expect(ops).not.toContain('_contains');
    });

    it('falls back to String for unknown types', () => {
        expect(getFilterOperators('Unknown')).toEqual(getFilterOperators('String'));
        expect(getFilterOperators('')).toEqual(getFilterOperators('String'));
    });
});

// ── getFilterObj ───────────────────────────────────────────────

describe('getFilterObj', () => {
    it('returns empty result for empty string', () => {
        const result = getFilterObj('');
        expect(result).toEqual({ variables: {}, param: '', filterText: '' });
    });

    it('returns empty result for null-like input', () => {
        const result = getFilterObj(undefined as unknown as string);
        expect(result).toEqual({ variables: {}, param: '', filterText: '' });
    });

    it('parses valid filter JSON', () => {
        const input = JSON.stringify(['name', '_eq', 'test', 'String']);
        const result = getFilterObj(input);
        expect(result.variables).toEqual({ filter: 'test' });
        expect(result.param).toBe(', $filter: String');
        expect(result.filterText).toBe('{name: {_eq: $filter} }');
    });

    it('handles invalid JSON gracefully', () => {
        const result = getFilterObj('not json');
        expect(result).toEqual({ variables: {}, param: '', filterText: '' });
    });

    it('handles malformed array gracefully', () => {
        const result = getFilterObj(JSON.stringify([1]));
        expect(result.param).toBe(', $filter: undefined');
    });
});

// ── toLocaleDate ───────────────────────────────────────────────

describe('toLocaleDate', () => {
    it('returns empty for empty string', () => {
        expect(toLocaleDate('')).toBe('');
    });

    it('returns empty for invalid date', () => {
        expect(toLocaleDate('not-a-date')).toBe('');
    });

    it('returns empty for dates before 1973', () => {
        expect(toLocaleDate('1970-01-01')).toBe('');
        expect(toLocaleDate('1900-01-01')).toBe('');
    });

    it('formats valid modern dates', () => {
        const result = toLocaleDate('2024-06-15T10:30:00');
        expect(result).toBeTruthy();
        expect(result.length).toBeGreaterThan(5);
    });

    it('handles ISO date strings', () => {
        expect(toLocaleDate('2024-01-01')).toBeTruthy();
    });

    it('handles null-like values', () => {
        expect(toLocaleDate(null as unknown as string)).toBe('');
        expect(toLocaleDate(undefined as unknown as string)).toBe('');
    });
});

// ── getRowPkValue ──────────────────────────────────────────────

describe('getRowPkValue', () => {
    const table = makeTable();

    it('returns the pk column value', () => {
        expect(getRowPkValue({ id: 42, name: 'test' }, table)).toBe('42');
    });

    it('returns string pk values', () => {
        expect(getRowPkValue({ id: 'abc-123', name: 'test' }, table)).toBe('abc-123');
    });

    it('falls back to row.id if no primary key defined', () => {
        const noPkTable = makeTable({ primaryKeys: [] });
        expect(getRowPkValue({ id: 99 }, noPkTable)).toBe('99');
    });

    it('returns empty string when pk column is missing from row', () => {
        // getRowPkValue uses ?? which treats undefined as empty
        expect(getRowPkValue({ name: 'test' }, table)).toBe('');
    });

    it('handles null/undefined row values', () => {
        // null/undefined coalesce to "" via ??
        expect(getRowPkValue({ id: null }, table)).toBe('');
        expect(getRowPkValue({ id: undefined }, table)).toBe('');
    });

    it('handles zero as pk', () => {
        expect(getRowPkValue({ id: 0 }, table)).toBe('0');
    });
});

// ── getGraphQlType ─────────────────────────────────────────────

describe('getGraphQlType', () => {
    it('maps Int correctly', () => expect(getGraphQlType('Int')).toBe('Int'));
    it('maps Int! correctly', () => expect(getGraphQlType('Int!')).toBe('Int'));
    it('maps Float correctly', () => expect(getGraphQlType('Float')).toBe('Float'));
    it('maps Boolean correctly', () => expect(getGraphQlType('Boolean')).toBe('Boolean'));
    it('maps DateTime to String', () => expect(getGraphQlType('DateTime')).toBe('String'));
    it('maps String to String', () => expect(getGraphQlType('String')).toBe('String'));
    it('maps unknown types to String', () => expect(getGraphQlType('Foo')).toBe('String'));
    it('strips ! from all types', () => {
        expect(getGraphQlType('Float!')).toBe('Float');
        expect(getGraphQlType('Boolean!')).toBe('Boolean');
    });
});

// ── buildColumnFilters ─────────────────────────────────────────

describe('buildColumnFilters', () => {
    const table = makeTable({
        columns: [
            makeColumn({ name: 'name', paramType: 'String' }),
            makeColumn({ name: 'age', paramType: 'Int' }),
            makeColumn({ name: 'active', paramType: 'Boolean' }),
            makeColumn({ name: 'score', paramType: 'Float' }),
        ],
    });

    it('returns empty for no filters', () => {
        const result = buildColumnFilters([], table);
        expect(result).toEqual({ variables: {}, params: [], filterTexts: [] });
    });

    it('builds simple equality filter', () => {
        const filters = [{ id: 'name', value: { operator: '_eq', value: 'test' } as ColumnFilterValue }];
        const result = buildColumnFilters(filters, table);
        expect(result.variables).toEqual({ cf_name: 'test' });
        expect(result.params).toEqual(['$cf_name: String']);
        expect(result.filterTexts).toEqual(['{name: {_eq: $cf_name}}']);
    });

    it('builds _null filter without variable', () => {
        const filters = [{ id: 'name', value: { operator: '_null', value: true } as ColumnFilterValue }];
        const result = buildColumnFilters(filters, table);
        expect(result.variables).toEqual({});
        expect(result.params).toEqual([]);
        expect(result.filterTexts).toEqual(['{name: {_null: true}}']);
    });

    it('builds _null false filter', () => {
        const filters = [{ id: 'name', value: { operator: '_null', value: false } as ColumnFilterValue }];
        const result = buildColumnFilters(filters, table);
        expect(result.filterTexts).toEqual(['{name: {_null: false}}']);
    });

    it('builds _between filter with two variables', () => {
        const filters = [{ id: 'age', value: { operator: '_between', value: [10, 20] } as ColumnFilterValue }];
        const result = buildColumnFilters(filters, table);
        expect(result.variables).toEqual({ cf_age_lo: 10, cf_age_hi: 20 });
        expect(result.params).toEqual(['$cf_age_lo: Int', '$cf_age_hi: Int']);
        expect(result.filterTexts).toEqual(['{age: {_between: [$cf_age_lo, $cf_age_hi]}}']);
    });

    it('skips filters with empty value', () => {
        const filters = [{ id: 'name', value: { operator: '_eq', value: '' } as ColumnFilterValue }];
        expect(buildColumnFilters(filters, table).filterTexts).toEqual([]);
    });

    it('skips filters with null value', () => {
        const filters = [{ id: 'name', value: { operator: '_eq', value: null } as ColumnFilterValue }];
        expect(buildColumnFilters(filters, table).filterTexts).toEqual([]);
    });

    it('skips filters for nonexistent columns', () => {
        const filters = [{ id: 'nonexistent', value: { operator: '_eq', value: 'x' } as ColumnFilterValue }];
        expect(buildColumnFilters(filters, table).filterTexts).toEqual([]);
    });

    it('skips _between with invalid range', () => {
        const filters = [{ id: 'age', value: { operator: '_between', value: [10] } as ColumnFilterValue }];
        expect(buildColumnFilters(filters, table).filterTexts).toEqual([]);
    });

    it('skips _between with non-array', () => {
        const filters = [{ id: 'age', value: { operator: '_between', value: 'not-array' } as ColumnFilterValue }];
        expect(buildColumnFilters(filters, table).filterTexts).toEqual([]);
    });

    it('handles multiple filters combined', () => {
        const filters = [
            { id: 'name', value: { operator: '_contains', value: 'test' } as ColumnFilterValue },
            { id: 'age', value: { operator: '_gt', value: 18 } as ColumnFilterValue },
        ];
        const result = buildColumnFilters(filters, table);
        expect(result.variables).toEqual({ cf_name: 'test', cf_age: 18 });
        expect(result.params.length).toBe(2);
        expect(result.filterTexts.length).toBe(2);
    });

    it('uses correct GraphQL type for each column', () => {
        const filters = [
            { id: 'score', value: { operator: '_gt', value: 3.14 } as ColumnFilterValue },
        ];
        const result = buildColumnFilters(filters, table);
        expect(result.params).toEqual(['$cf_score: Float']);
    });
});

// ── serializeColumnFilters / deserializeColumnFilters ──────────

describe('serializeColumnFilters', () => {
    it('returns empty string for empty filters', () => {
        expect(serializeColumnFilters([])).toBe('');
    });

    it('serializes filters as compact JSON', () => {
        const filters = [{ id: 'name', value: { operator: '_eq', value: 'test' } as ColumnFilterValue }];
        const result = serializeColumnFilters(filters);
        expect(JSON.parse(result)).toEqual([['name', '_eq', 'test']]);
    });

    it('round-trips through deserialize', () => {
        const original = [
            { id: 'name', value: { operator: '_contains', value: 'foo' } as ColumnFilterValue },
            { id: 'age', value: { operator: '_between', value: [1, 10] } as ColumnFilterValue },
        ];
        const serialized = serializeColumnFilters(original);
        const deserialized = deserializeColumnFilters(serialized);
        expect(deserialized.length).toBe(2);
        expect((deserialized[0].value as ColumnFilterValue).operator).toBe('_contains');
        expect((deserialized[1].value as ColumnFilterValue).value).toEqual([1, 10]);
    });
});

describe('deserializeColumnFilters', () => {
    it('returns empty array for empty string', () => {
        expect(deserializeColumnFilters('')).toEqual([]);
    });

    it('returns empty array for null-like', () => {
        expect(deserializeColumnFilters(null as unknown as string)).toEqual([]);
        expect(deserializeColumnFilters(undefined as unknown as string)).toEqual([]);
    });

    it('returns empty array for invalid JSON', () => {
        expect(deserializeColumnFilters('not json')).toEqual([]);
    });

    it('parses valid serialized filters', () => {
        const input = JSON.stringify([['name', '_eq', 'test']]);
        const result = deserializeColumnFilters(input);
        expect(result).toEqual([{ id: 'name', value: { operator: '_eq', value: 'test' } }]);
    });
});

// ── getPkType ──────────────────────────────────────────────────

describe('getPkType', () => {
    it('returns Int for Int pk column', () => {
        expect(getPkType(makeTable())).toBe('Int');
    });

    it('strips ! from type', () => {
        const table = makeTable({
            columns: [makeColumn({ name: 'id', paramType: 'Int!', isPrimaryKey: true })],
        });
        expect(getPkType(table)).toBe('Int');
    });

    it('returns String for String pk column', () => {
        const table = makeTable({
            primaryKeys: ['uuid'],
            columns: [makeColumn({ name: 'uuid', paramType: 'String!', isPrimaryKey: true })],
        });
        expect(getPkType(table)).toBe('String');
    });

    it('defaults to String when PK column is declared but not in the columns list', () => {
        // getPkType used to default to Int for this edge case; the unified implementation
        // now prefers String so an unknown PK is handled as opaque text rather than silently
        // coerced to a number. Real schemas always have the PK column present.
        const table = makeTable({ primaryKeys: ['missing'] });
        expect(getPkType(table)).toBe('String');
    });

    it('defaults to Int when no pk defined', () => {
        const table = makeTable({ primaryKeys: [] });
        expect(getPkType(table)).toBe('Int');
    });
});

// ── buildQuery ─────────────────────────────────────────────────

describe('buildQuery', () => {
    const table = makeTable({
        name: 'courses',
        graphQlName: 'courses',
        columns: [
            makeColumn({ name: 'course_id', paramType: 'Int!', isPrimaryKey: true }),
            makeColumn({ name: 'name', paramType: 'String!' }),
            makeColumn({ name: 'credits', paramType: 'Int' }),
        ],
        primaryKeys: ['course_id'],
        multiJoins: [],
        singleJoins: [],
    });
    const schema = makeSchema([table]);

    it('returns null for null table', () => {
        expect(buildQuery(null as unknown as Table, schema, '', [])).toBeNull();
    });

    it('returns null when schema has no data', () => {
        const emptySchema = { ...schema, data: null as unknown as Table[] };
        expect(buildQuery(table, emptySchema, '', [])).toBeNull();
    });

    it('returns null when table not found in schema', () => {
        const otherSchema = makeSchema([makeTable({ name: 'other', graphQlName: 'other' })]);
        expect(buildQuery(table, otherSchema, '', [])).toBeNull();
    });

    it('generates basic query with all columns', () => {
        const q = buildQuery(table, schema, '', [])!;
        expect(q).toContain('query Getcourses');
        expect(q).toContain('course_id');
        expect(q).toContain('name');
        expect(q).toContain('credits');
        expect(q).toContain('$sort: [coursesSortEnum!]');
        expect(q).toContain('$limit: Int');
        expect(q).toContain('$offset: Int');
        expect(q).toContain('total offset limit data');
    });

    it('includes filter text from filter string', () => {
        const filter = JSON.stringify(['name', '_eq', 'test', 'String']);
        const q = buildQuery(table, schema, filter, [])!;
        expect(q).toContain('filter:');
        expect(q).toContain('$filter: String');
    });

    it('includes column filter params and text', () => {
        const cf = [{ id: 'credits', value: { operator: '_gt', value: 3 } as ColumnFilterValue }];
        const q = buildQuery(table, schema, '', cf)!;
        expect(q).toContain('$cf_credits: Int');
        expect(q).toContain('{credits: {_gt: $cf_credits}}');
    });

    it('combines filter string and column filters with AND', () => {
        const filter = JSON.stringify(['name', '_eq', 'test', 'String']);
        const cf = [{ id: 'credits', value: { operator: '_gt', value: 3 } as ColumnFilterValue }];
        const q = buildQuery(table, schema, filter, cf)!;
        expect(q).toContain('{and: [');
    });

    it('generates id filter for single record lookup', () => {
        const q = buildQuery(table, schema, '', [], '42')!;
        expect(q).toContain('$id: Int');
        expect(q).toContain('{ course_id: { _eq: $id}}');
    });

    it('generates FK filter for child table view', () => {
        const childTable = makeTable({
            name: 'assignments',
            graphQlName: 'assignments',
            columns: [
                makeColumn({ name: 'assignment_id', paramType: 'Int!', isPrimaryKey: true }),
                makeColumn({ name: 'course_id', paramType: 'Int' }),
                makeColumn({ name: 'title', paramType: 'String' }),
            ],
            primaryKeys: ['assignment_id'],
            singleJoins: [{ name: 'courses', sourceColumnNames: ['course_id'], destinationTable: 'courses', destinationColumnNames: ['course_id'] }],
        });
        const childSchema = makeSchema([table, childTable]);
        const q = buildQuery(childTable, childSchema, '', [], '5', 'courses')!;
        expect(q).toContain('$id: Int');
        expect(q).toContain('{ course_id: { _eq: $id}}');
    });

    it('generates FK filter with explicit filterColumn', () => {
        const childTable = makeTable({
            name: 'enrollments',
            graphQlName: 'enrollments',
            columns: [
                makeColumn({ name: 'enrollment_id', paramType: 'Int!', isPrimaryKey: true }),
                makeColumn({ name: 'course_id', paramType: 'Int' }),
            ],
            primaryKeys: ['enrollment_id'],
        });
        const childSchema = makeSchema([table, childTable]);
        const q = buildQuery(childTable, childSchema, '', [], '5', 'courses', 'course_id')!;
        expect(q).toContain('course_id: { _eq: $id}');
    });

    it('includes multi-join child fields', () => {
        const assignments = makeTable({
            name: 'assignments',
            graphQlName: 'assignments',
            labelColumn: 'title',
            primaryKeys: ['assignment_id'],
            columns: [
                makeColumn({ name: 'assignment_id', paramType: 'Int!', isPrimaryKey: true }),
                makeColumn({ name: 'title', paramType: 'String' }),
            ],
        });
        const tableWithJoins = makeTable({
            ...table,
            multiJoins: [{ name: 'assignments', sourceColumnNames: ['course_id'], destinationTable: 'assignments', destinationColumnNames: ['course_id'] }],
        });
        const joinSchema = makeSchema([tableWithJoins, assignments]);
        const q = buildQuery(tableWithJoins, joinSchema, '', [])!;
        expect(q).toContain('assignments { assignment_id title }');
    });

    it('handles multi-join when label column equals pk', () => {
        const simple = makeTable({
            name: 'tags',
            graphQlName: 'tags',
            labelColumn: 'id',
            primaryKeys: ['id'],
            columns: [makeColumn({ name: 'id', paramType: 'Int!', isPrimaryKey: true })],
        });
        const tableWithJoins = makeTable({
            ...table,
            multiJoins: [{ name: 'tags', sourceColumnNames: ['id'], destinationTable: 'tags', destinationColumnNames: ['id'] }],
        });
        const joinSchema = makeSchema([tableWithJoins, simple]);
        const q = buildQuery(tableWithJoins, joinSchema, '', [])!;
        // Should not duplicate id column
        expect(q).toContain('tags { id }');
    });

    it('handles table with no columns', () => {
        const emptyTable = makeTable({ columns: [] });
        const q = buildQuery(emptyTable, makeSchema([emptyTable]), '', []);
        expect(q).toBeTruthy();
    });
});

// ── Composite primary key support ──────────────────────────────

describe('getPkTypes', () => {
    it('returns one entry per PK column in declaration order', () => {
        const table = makeTable({
            primaryKeys: ['student_id', 'course_id'],
            columns: [
                makeColumn({ name: 'student_id', paramType: 'Int!', isPrimaryKey: true }),
                makeColumn({ name: 'course_id', paramType: 'String!', isPrimaryKey: true }),
                makeColumn({ name: 'grade', paramType: 'String' }),
            ],
        });
        expect(getPkTypes(table)).toEqual([
            { name: 'student_id', gqlType: 'Int' },
            { name: 'course_id', gqlType: 'String' },
        ]);
    });

    it('returns a single entry for a simple PK', () => {
        expect(getPkTypes(makeTable())).toEqual([{ name: 'id', gqlType: 'Int' }]);
    });

    it('returns an empty array when the table has no primary keys', () => {
        const table = makeTable({ primaryKeys: [] });
        expect(getPkTypes(table)).toEqual([]);
    });

    it('falls back to String when the PK column is missing from columns', () => {
        const table = makeTable({
            primaryKeys: ['missing'],
            columns: [makeColumn({ name: 'name', paramType: 'String' })],
        });
        expect(getPkTypes(table)).toEqual([{ name: 'missing', gqlType: 'String' }]);
    });
});

describe('buildPkEqVariables', () => {
    it('returns a single { id: coerced } entry for a single-PK table', () => {
        const table = makeTable({
            primaryKeys: ['id'],
            columns: [makeColumn({ name: 'id', paramType: 'Int!', isPrimaryKey: true })],
        });
        expect(buildPkEqVariables('42', table)).toEqual({ id: 42 });
    });

    it('coerces string PK values as string for String PKs', () => {
        const table = makeTable({
            primaryKeys: ['code'],
            columns: [makeColumn({ name: 'code', paramType: 'String!', isPrimaryKey: true })],
        });
        expect(buildPkEqVariables('CS-101', table)).toEqual({ id: 'CS-101' });
    });

    it('returns one pk_${name} variable per column for composite PKs', () => {
        const table = makeTable({
            primaryKeys: ['student_id', 'course_id'],
            columns: [
                makeColumn({ name: 'student_id', paramType: 'Int!', isPrimaryKey: true }),
                makeColumn({ name: 'course_id', paramType: 'String!', isPrimaryKey: true }),
            ],
        });
        expect(buildPkEqVariables('1::cs-101', table)).toEqual({
            pk_student_id: 1,
            pk_course_id: 'cs-101',
        });
    });
});

describe('buildQuery — composite primary keys', () => {
    const enrollment = makeTable({
        name: 'enrollment',
        graphQlName: 'enrollment',
        primaryKeys: ['student_id', 'course_id'],
        columns: [
            makeColumn({ name: 'student_id', paramType: 'Int!', isPrimaryKey: true }),
            makeColumn({ name: 'course_id', paramType: 'String!', isPrimaryKey: true }),
            makeColumn({ name: 'grade', paramType: 'String' }),
        ],
        singleJoins: [],
        multiJoins: [],
    });
    const schema = makeSchema([enrollment]);

    it('emits one $pk_${name} variable per PK column when querying by id', () => {
        const q = buildQuery(enrollment, schema, '', [], '1::cs-101')!;
        expect(q).toContain('$pk_student_id: Int');
        expect(q).toContain('$pk_course_id: String');
    });

    it('wraps the composite-PK filter in {and: [...]}', () => {
        const q = buildQuery(enrollment, schema, '', [], '1::cs-101')!;
        expect(q).toContain('{and: [{student_id: {_eq: $pk_student_id}}, {course_id: {_eq: $pk_course_id}}]}');
    });

    it('does not use the legacy single $id variable for composite PKs', () => {
        const q = buildQuery(enrollment, schema, '', [], '1::cs-101')!;
        expect(q).not.toContain('$id: Int');
        expect(q).not.toMatch(/\$id:\s/);
    });

    it('still generates the legacy $id form for single-PK tables', () => {
        const single = makeTable({
            primaryKeys: ['id'],
            columns: [makeColumn({ name: 'id', paramType: 'Int!', isPrimaryKey: true })],
        });
        const q = buildQuery(single, makeSchema([single]), '', [], '42')!;
        expect(q).toContain('$id: Int');
        expect(q).toContain('{ id: { _eq: $id}}');
    });

    it('emits all PK columns of a composite-PK destination in a multiJoin child query', () => {
        const composite = makeTable({
            name: 'enrollment',
            graphQlName: 'enrollment',
            labelColumn: 'grade',
            primaryKeys: ['student_id', 'course_id'],
            columns: [
                makeColumn({ name: 'student_id', paramType: 'Int!', isPrimaryKey: true }),
                makeColumn({ name: 'course_id', paramType: 'String!', isPrimaryKey: true }),
                makeColumn({ name: 'grade', paramType: 'String' }),
            ],
        });
        const parent = makeTable({
            name: 'students',
            graphQlName: 'students',
            primaryKeys: ['student_id'],
            columns: [makeColumn({ name: 'student_id', paramType: 'Int!', isPrimaryKey: true })],
            multiJoins: [{
                name: 'enrollment',
                sourceColumnNames: ['student_id'],
                destinationTable: 'enrollment',
                destinationColumnNames: ['student_id'],
            }],
        });
        const parentSchema = makeSchema([parent, composite]);
        const q = buildQuery(parent, parentSchema, '', [])!;
        expect(q).toContain('enrollment { student_id course_id grade }');
    });

    it('handles a nested parent-table filter with a composite parent PK', () => {
        const parent = makeTable({
            name: 'enrollment',
            graphQlName: 'enrollment',
            primaryKeys: ['student_id', 'course_id'],
            columns: [
                makeColumn({ name: 'student_id', paramType: 'Int!', isPrimaryKey: true }),
                makeColumn({ name: 'course_id', paramType: 'String!', isPrimaryKey: true }),
            ],
        });
        const child = makeTable({
            name: 'grades',
            graphQlName: 'grades',
            primaryKeys: ['grade_id'],
            columns: [makeColumn({ name: 'grade_id', paramType: 'Int!', isPrimaryKey: true })],
        });
        const parentSchema = makeSchema([parent, child]);
        // tableFilter='enrollment' but no filterColumn or matching singleJoin — falls into the nested-parent path.
        const q = buildQuery(child, parentSchema, '', [], '1::cs-101', 'enrollment')!;
        expect(q).toContain('$pk_student_id: Int');
        expect(q).toContain('$pk_course_id: String');
        expect(q).toContain('{ enrollment: {and: [{student_id: {_eq: $pk_student_id}}, {course_id: {_eq: $pk_course_id}}]}}');
    });
});
