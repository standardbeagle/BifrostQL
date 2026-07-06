import { describe, it, expect } from 'vitest';
import { buildPreviewPlan } from './fk-cell-popover';
import type { Column, Table } from '../types/schema';

function col(name: string, paramType = 'String', opts: Partial<Column> = {}): Column {
    return {
        dbName: name,
        graphQlName: name,
        name,
        label: name,
        paramType,
        dbType: 'nvarchar',
        isPrimaryKey: false,
        isIdentity: false,
        isNullable: true,
        isReadOnly: false,
        metadata: {},
        ...opts,
    };
}

function table(name: string, columns: Column[], primaryKeys: string[] = ['id']): Table {
    return {
        dbName: name,
        graphQlName: name,
        name,
        label: name,
        labelColumn: '',
        primaryKeys,
        isEditable: true,
        metadata: {},
        columns,
        multiJoins: [],
        singleJoins: [],
    };
}

describe('buildPreviewPlan', () => {
    it('declares $id with the FK column type and coerces the value (Int)', () => {
        const dest = table('customers', [
            col('customer_id', 'Int!', { isPrimaryKey: true }),
            col('name'),
        ], ['customer_id']);
        const preview = [dest.columns[1]];

        const plan = buildPreviewPlan('customers', dest, preview, undefined, undefined, '42', 'customer_id');

        expect(plan).not.toBeNull();
        expect(plan!.query).toContain('query FkPreview($id: Int)');
        expect(plan!.variables).toEqual({ id: 42 });
    });

    it('declares $id: Boolean and sends a boolean for a Boolean FK', () => {
        // Regression: the popover used to fall back to String for Boolean FK
        // columns, sending {id: "true"} under a String declaration.
        const dest = table('flags', [
            col('is_active', 'Boolean!', { isPrimaryKey: true }),
            col('description'),
        ], ['is_active']);
        const preview = [dest.columns[1]];

        const plan = buildPreviewPlan('flags', dest, preview, undefined, undefined, 'true', 'is_active');

        expect(plan).not.toBeNull();
        expect(plan!.query).toContain('query FkPreview($id: Boolean)');
        expect(plan!.variables).toEqual({ id: true });
    });

    it('keeps string ids as strings under a String declaration', () => {
        const dest = table('codes', [
            col('code', 'String!', { isPrimaryKey: true }),
            col('label_text'),
        ], ['code']);
        const preview = [dest.columns[1]];

        const plan = buildPreviewPlan('codes', dest, preview, undefined, undefined, '0123', 'code');

        expect(plan).not.toBeNull();
        expect(plan!.query).toContain('query FkPreview($id: String)');
        expect(plan!.variables).toEqual({ id: '0123' });
    });

    it('returns null when there are no preview columns', () => {
        const dest = table('customers', [col('customer_id', 'Int!', { isPrimaryKey: true })], ['customer_id']);
        expect(buildPreviewPlan('customers', dest, [], undefined, undefined, 1, 'customer_id')).toBeNull();
    });
});
