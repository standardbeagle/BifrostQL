import { describe, it, expect } from 'vitest';
import { buildFkEqFilter, findJoinBySource, fkSourceValues, isComposite, isFkMember } from './fk';
import type { Column, Join, Table } from '../types/schema';

function col(name: string, paramType = 'String'): Column {
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
    };
}

function destTable(cols: Column[]): Pick<Table, 'columns'> {
    return { columns: cols };
}

const singleJoin: Join = {
    name: 'customers',
    sourceColumnNames: ['customer_id'],
    destinationTable: 'customers',
    destinationColumnNames: ['customer_id'],
};

const compositeJoin: Join = {
    name: 'orders',
    sourceColumnNames: ['tenant_id', 'order_id'],
    destinationTable: 'orders',
    destinationColumnNames: ['tenant_id', 'order_id'],
};

describe('isComposite', () => {
    it('returns false for single-column joins', () => {
        expect(isComposite(singleJoin)).toBe(false);
    });
    it('returns true when the join spans more than one column', () => {
        expect(isComposite(compositeJoin)).toBe(true);
    });
});

describe('isFkMember', () => {
    it('matches any column in sourceColumnNames', () => {
        expect(isFkMember(compositeJoin, 'tenant_id')).toBe(true);
        expect(isFkMember(compositeJoin, 'order_id')).toBe(true);
        expect(isFkMember(compositeJoin, 'qty')).toBe(false);
    });
});

describe('findJoinBySource', () => {
    it('finds a join even when the column is not the first FK source column', () => {
        expect(findJoinBySource([compositeJoin], 'order_id')).toBe(compositeJoin);
    });
    it('returns undefined when no join references the column', () => {
        expect(findJoinBySource([singleJoin], 'qty')).toBeUndefined();
    });
});

describe('buildFkEqFilter', () => {
    it('emits a single-column _eq filter for single FKs', () => {
        const dest = destTable([col('customer_id', 'Int!')]);
        const result = buildFkEqFilter({ customer_id: 42 }, singleJoin, dest);
        expect(result).not.toBeNull();
        expect(result!.filterText).toBe('{customer_id: {_eq: $fk_customer_id}}');
        expect(result!.variables).toEqual({ fk_customer_id: 42 });
        expect(result!.params).toEqual(['$fk_customer_id: Int']);
    });

    it('emits an {and: [...]} composite filter mapping source values to destination columns', () => {
        const dest = destTable([
            col('tenant_id', 'Int!'),
            col('order_id', 'String!'),
        ]);
        const sourceRow = { tenant_id: 1, order_id: 'ORD-7', qty: 3 };
        const result = buildFkEqFilter(sourceRow, compositeJoin, dest);
        expect(result).not.toBeNull();
        expect(result!.filterText).toBe(
            '{and: [{tenant_id: {_eq: $fk_tenant_id}}, {order_id: {_eq: $fk_order_id}}]}',
        );
        expect(result!.variables).toEqual({ fk_tenant_id: 1, fk_order_id: 'ORD-7' });
        expect(result!.params).toEqual(['$fk_tenant_id: Int', '$fk_order_id: String']);
    });

    it('returns null when a source-side column is missing from the row', () => {
        const dest = destTable([
            col('tenant_id', 'Int!'),
            col('order_id', 'String!'),
        ]);
        const result = buildFkEqFilter({ tenant_id: 1 }, compositeJoin, dest);
        expect(result).toBeNull();
    });

    it('honors a custom variable prefix to avoid collisions', () => {
        const dest = destTable([col('customer_id', 'Int!')]);
        const result = buildFkEqFilter({ customer_id: 7 }, singleJoin, dest, 'parent');
        expect(result!.params).toEqual(['$parent_customer_id: Int']);
        expect(result!.variables).toEqual({ parent_customer_id: 7 });
    });
});

describe('fkSourceValues', () => {
    it('returns the subset of the row that matches sourceColumnNames', () => {
        expect(fkSourceValues({ tenant_id: 1, order_id: 'A', qty: 4 }, compositeJoin)).toEqual({
            tenant_id: 1,
            order_id: 'A',
        });
    });

    it('returns null if any FK source column is missing from the row', () => {
        expect(fkSourceValues({ tenant_id: 1 }, compositeJoin)).toBeNull();
    });
});
