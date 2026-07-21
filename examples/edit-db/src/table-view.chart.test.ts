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
});
