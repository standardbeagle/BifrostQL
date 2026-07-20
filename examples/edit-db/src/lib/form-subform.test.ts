import { describe, expect, it } from 'vitest';
import { childFilterPlan, childPrefill, relationshipIsUsable } from './form-subform';
import type { Column, Join, Table } from '../types/schema';

const column = (name: string): Column => ({ dbName: name, graphQlName: name, name, label: name, paramType: 'Int!', dbType: 'int', isPrimaryKey: false, isIdentity: false, isNullable: false, isReadOnly: false, metadata: {} });
const table = (name: string, columns: string[]): Table => ({ dbName: name, graphQlName: name, name, label: name, labelColumn: columns[0] ?? '', primaryKeys: ['id'], isEditable: true, metadata: {}, columns: columns.map(column), multiJoins: [], singleJoins: [] });

describe('form subform relationship plans', () => {
  const orders = table('orders', ['tenant_id', 'order_id']);
  const lines = table('order_lines', ['tenant_id', 'order_id', 'line_no']);
  const join: Join = { name: 'lines', fieldName: 'lines', sourceColumnNames: ['tenant_id', 'order_id'], destinationTable: 'order_lines', destinationColumnNames: ['tenant_id', 'order_id'] };

  it('binds every composite parent/FK pair for the child filter and create prefill', () => {
    const row = { tenant_id: 7, order_id: 42 };
    expect(childFilterPlan(orders, lines, join, row)).toEqual({
      params: ['$parent_tenant_id: Int', '$parent_order_id: Int'],
      filter: '{and: [{tenant_id: {_eq: $parent_tenant_id}}, {order_id: {_eq: $parent_order_id}}]}',
      variables: { parent_tenant_id: 7, parent_order_id: 42 },
    });
    expect(childPrefill(join, row)).toEqual({ tenant_id: 7, order_id: 42 });
  });

  it('does not silently use only the first FK column when relationship metadata is incomplete', () => {
    const broken: Join = { ...join, destinationColumnNames: ['tenant_id'] };
    expect(relationshipIsUsable(orders, lines, broken)).toBe(false);
    expect(childFilterPlan(orders, lines, broken, { tenant_id: 7, order_id: 42 })).toBeNull();
  });
});
