import { describe, it, expect } from 'vitest';
import type { Column, Table } from '../types/schema';
import {
    refLabelColumn,
    tableRefPlan,
    tableRefLookupPlan,
    coerceKeyValue,
} from './table-ref';

function col(name: string, opts: Partial<Column> = {}): Column {
    return {
        dbName: name, graphQlName: name, name, label: name, paramType: 'String',
        dbType: 'nvarchar', isPrimaryKey: false, isIdentity: false, isNullable: true,
        isReadOnly: false, metadata: {}, ...opts,
    };
}

function table(name: string, opts: Partial<Table> = {}): Table {
    return {
        dbName: name, graphQlName: name, name, label: name, labelColumn: 'name',
        primaryKeys: ['pk'], isEditable: true, metadata: {},
        columns: [col('pk', { isPrimaryKey: true, paramType: 'Int!' }), col('name')],
        multiJoins: [], singleJoins: [], ...opts,
    };
}

describe('refLabelColumn', () => {
    it('uses the schema labelColumn when set', () => {
        expect(refLabelColumn(table('users'))).toBe('name');
    });

    it('falls back to the first primary-key column, never a guessed `id` literal', () => {
        expect(refLabelColumn(table('users', { labelColumn: '' }))).toBe('pk');
    });

    it('falls back to the first column when the table has no primary key', () => {
        const t = table('logs', { labelColumn: '', primaryKeys: [], columns: [col('message')] });
        expect(refLabelColumn(t)).toBe('message');
    });

    it('fails fast when no column is derivable', () => {
        const t = table('void', { labelColumn: '', primaryKeys: [], columns: [] });
        expect(() => refLabelColumn(t)).toThrow(/no usable label column/);
    });
});

describe('tableRefPlan', () => {
    it('builds a sorted, limited list query keyed and labeled from the schema', () => {
        const plan = tableRefPlan(table('users'), 'pk');
        expect(plan.serverSearch).toBe(true); // String label column
        expect(plan.labelColumn).toBe('name');
        expect(plan.query).toContain('key: pk');
        expect(plan.query).toContain('label: name');
        expect(plan.query).toContain('sort: [name_asc]');
        expect(plan.query).toContain('limit: $limit');
        expect(plan.query).not.toContain('_contains');
    });

    it('adds a server-side _contains filter when searching a String label column', () => {
        const plan = tableRefPlan(table('users'), 'pk', 'ali');
        expect(plan.serverSearch).toBe(true);
        expect(plan.query).toContain('$search: String');
        expect(plan.query).toContain('filter: {name: {_contains: $search}}');
    });

    it('reports client-side filtering for a non-String label column and omits the filter', () => {
        const t = table('orders', {
            labelColumn: 'orderNo',
            columns: [col('pk', { isPrimaryKey: true, paramType: 'Int!' }), col('orderNo', { paramType: 'Int' })],
        });
        const plan = tableRefPlan(t, 'pk', '42');
        expect(plan.serverSearch).toBe(false);
        expect(plan.query).not.toContain('_contains');
        expect(plan.query).not.toContain('$search');
    });

    it('never emits a bare `label:` or a guessed id column for an empty labelColumn', () => {
        const plan = tableRefPlan(table('users', { labelColumn: '' }), 'pk');
        expect(plan.query).toContain('label: pk');
        expect(plan.query).not.toMatch(/label:\s*\n/);
        expect(plan.query).not.toContain('label: id');
    });
});

describe('tableRefLookupPlan', () => {
    it('builds a single-row lookup typed from the key column', () => {
        const plan = tableRefLookupPlan(table('users'), 'pk');
        expect(plan.keyType).toBe('Int'); // Int! stripped
        expect(plan.query).toContain('$key: Int');
        expect(plan.query).toContain('filter: {pk: {_eq: $key}}');
        expect(plan.query).toContain('limit: 1');
        expect(plan.query).toContain('label: name');
    });
});

describe('coerceKeyValue', () => {
    it('coerces numeric key types to numbers and leaves strings as strings', () => {
        expect(coerceKeyValue('Int', '5')).toBe(5);
        expect(coerceKeyValue('BigInt', '9007')).toBe(9007);
        expect(coerceKeyValue('String', 5)).toBe('5');
        expect(coerceKeyValue('String', 'abc')).toBe('abc');
    });
});
