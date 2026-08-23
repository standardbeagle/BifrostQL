import { describe, it, expect } from 'vitest';
import { buildBulkUpdatePayloads } from './mutation-payload';
import type { Column } from '../types/schema';

function col(name: string, paramType: string, opts: Partial<Column> = {}): Column {
    return {
        dbName: name,
        graphQlName: name,
        name,
        label: name,
        paramType,
        dbType: '',
        isPrimaryKey: false,
        isIdentity: false,
        isNullable: false,
        isReadOnly: false,
        metadata: {},
        ...opts,
    } as Column;
}

describe('buildBulkUpdatePayloads', () => {
    const orderId = col('order_id', 'BigInt!', { isPrimaryKey: true });
    const lineNo = col('line_no', 'Int!', { isPrimaryKey: true });
    const status = col('status', 'String!');
    const qty = col('qty', 'Int!');
    const table = { primaryKeys: ['order_id', 'line_no'] };

    it('echoes the snapshot, overlays the change set, and keys each payload from its own row', () => {
        const payloads = buildBulkUpdatePayloads(
            table,
            [status, qty],
            [orderId, lineNo],
            [
                // BigInt PK above 2^53 arrives as a string and must stay one.
                { order_id: '9007199254740993', line_no: 1, status: 'new', qty: 5 },
                { order_id: '9007199254740993', line_no: 2, status: 'old', qty: 9 },
            ],
            { status: 'archived' },
        );

        expect(payloads).toEqual([
            { order_id: '9007199254740993', line_no: 1, status: 'archived', qty: 5 },
            { order_id: '9007199254740993', line_no: 2, status: 'archived', qty: 9 },
        ]);
        // Precision proof: the key survived as a string, not a rounded number.
        expect(typeof payloads[0].order_id).toBe('string');
    });

    it('throws when a snapshot cannot resolve its primary key — never a partial stage', () => {
        expect(() => buildBulkUpdatePayloads(
            table, [status], [orderId, lineNo],
            [{ order_id: '1', line_no: 1, status: 'a' }, { order_id: '2', status: 'b' }],
            { status: 'x' },
        )).toThrow(/primary key/);
    });

    it('coerces changed values by column type (numeric change arrives as text)', () => {
        const payloads = buildBulkUpdatePayloads(
            table, [status, qty], [orderId, lineNo],
            [{ order_id: '1', line_no: 1, status: 'a', qty: 2 }],
            { qty: '42' },
        );
        expect(payloads[0].qty).toBe(42);
    });
});
