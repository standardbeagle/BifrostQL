import { describe, expect, it } from 'vitest';
import { chartFilter } from './table-view';
import type { Table } from './types/schema';

const table = { graphQlName: 'orders', name: 'orders', columns: [
  { name: 'status', graphQlName: 'status', paramType: 'String' },
  { name: 'amount', graphQlName: 'amount', paramType: 'Decimal' },
] } as unknown as Table;

describe('chartFilter', () => {
  it('retains active grid filters as aggregate-query variables', () => {
    expect(chartFilter([{ id: 'status', value: { operator: '_eq', value: 'paid' } }], table)).toEqual({ status: { _eq: 'paid' } });
  });

  it('drops unknown columns rather than turning user strings into GraphQL fields', () => {
    expect(chartFilter([{ id: 'status) { evil', value: { operator: '_eq', value: 'paid' } }], table)).toBeUndefined();
  });

  it('carries a composite FK tuple filter into the aggregate filter as an AND of every column', () => {
    const lines = { graphQlName: 'orderLines', name: 'order_lines', columns: [
      { name: 'order_region', graphQlName: 'order_region', paramType: 'String' },
      { name: 'order_no', graphQlName: 'order_no', paramType: 'BigInt' },
    ] } as unknown as Table;
    expect(chartFilter([{ id: 'order_region', value: { operator: '_fk_tuple', value: [['order_region', 'eu'], ['order_no', '9007199254740993']] } }], lines))
      .toEqual({ and: [{ order_region: { _eq: 'eu' } }, { order_no: { _eq: '9007199254740993' } }] });
  });
});
