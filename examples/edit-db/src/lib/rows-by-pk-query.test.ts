import { describe, it, expect } from 'vitest';
import { buildRowsByPkQuery } from './query-builder';
import type { Column } from '../types/schema';

function col(name: string, paramType: string, isPrimaryKey = false): Column {
    return {
        dbName: name,
        graphQlName: name,
        name,
        label: name,
        paramType,
        dbType: '',
        isPrimaryKey,
        isIdentity: false,
        isNullable: false,
        isReadOnly: false,
        metadata: {},
    } as Column;
}

describe('buildRowsByPkQuery', () => {
    const table = {
        name: 'orders',
        primaryKeys: ['order_id', 'line_no'],
        columns: [col('order_id', 'BigInt!', true), col('line_no', 'Int!', true), col('status', 'String')],
    };

    it('builds one or-of-composite-pk query with every key column per row', () => {
        const result = buildRowsByPkQuery(table, [
            { order_id: '9007199254740993', line_no: 1 },
            { order_id: '7', line_no: 2 },
        ], ['order_id', 'line_no', 'status']);

        expect(result).not.toBeNull();
        expect(result!.query).toContain('query GetRowsByPk_orders(');
        expect(result!.query).toContain('$pk0_order_id: BigInt');
        expect(result!.query).toContain('$pk1_line_no: Int');
        expect(result!.query).toContain(
            '{or: [{and: [{order_id: {_eq: $pk0_order_id}}, {line_no: {_eq: $pk0_line_no}}]}, ' +
            '{and: [{order_id: {_eq: $pk1_order_id}}, {line_no: {_eq: $pk1_line_no}}]}]}');
        expect(result!.query).toContain('limit: 2');
        // BigInt keys stay strings through the round trip; Int keys become numbers.
        expect(result!.variables.pk0_order_id).toBe('9007199254740993');
        expect(result!.variables.pk0_line_no).toBe(1);
    });

    it('returns null for empty inputs rather than an unbounded query', () => {
        expect(buildRowsByPkQuery(table, [], ['status'])).toBeNull();
        expect(buildRowsByPkQuery(table, [{ order_id: '1', line_no: 1 }], [])).toBeNull();
        expect(buildRowsByPkQuery({ ...table, primaryKeys: [] }, [{ x: 1 }], ['status'])).toBeNull();
    });

    it('refuses hostile identifiers before building any text', () => {
        expect(() => buildRowsByPkQuery(
            { ...table, name: 'orders){x}' }, [{ order_id: '1', line_no: 1 }], ['status'],
        )).toThrow();
        expect(() => buildRowsByPkQuery(
            table, [{ order_id: '1', line_no: 1 }], ['status) { evil'],
        )).toThrow();
    });
});
