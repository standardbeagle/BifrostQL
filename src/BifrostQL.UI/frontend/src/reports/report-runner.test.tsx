import { describe, expect, it, vi } from 'vitest';
import { buildReportCsv, runReport } from './report-runner';
import type { GraphQLFetcher } from '@standardbeagle/edit-db';

const definition = {
  source: { kind: 'table' as const, table: 'orders' },
  columns: [{ column: 'region' }, { column: 'owner' }, { column: 'amount' }],
  groupBands: [
    { column: 'region', totals: [{ column: 'amount', op: 'sum' as const }] },
    { column: 'owner', totals: [{ column: 'amount', op: 'sum' as const }] },
  ],
  grandTotals: [{ column: 'amount', op: 'sum' as const }],
  pageHeader: { title: 'Orders' },
};

describe('runReport', () => {
  it('uses grouped aggregate results for two-level subtotal and grand totals, never page-row sums', async () => {
    const query = vi.fn(async (text: string, variables?: Record<string, unknown>) => {
      if (text.includes('ordersAggregate')) {
        return {
          groups: [
            { region: 'east', owner: 'a', _sum: { amount: 7 } },
            { region: 'east', owner: 'b', _sum: { amount: 3 } },
            { region: 'west', owner: 'c', _sum: { amount: 11 } },
          ],
          grand: [{ _sum: { amount: 21 } }],
        };
      }
      const offset = variables?.offset as number;
      return { orders: { total: 3, data: offset === 0 ? [
        { region: 'east', owner: 'a', amount: 999 },
        { region: 'east', owner: 'b', amount: 999 },
      ] : [{ region: 'west', owner: 'c', amount: 999 }] } };
    });

    const result = await runReport({ query } as GraphQLFetcher, definition, 2);

    expect(result.groupTotals.get('east\u0000a')?.['_sum.amount']).toBe(7);
    expect(result.groupTotals.get('east\u0000b')?.['_sum.amount']).toBe(3);
    expect(result.groupTotals.get('west\u0000c')?.['_sum.amount']).toBe(11);
    expect(result.grandTotals['_sum.amount']).toBe(21);
    expect(result.rows).toHaveLength(3);
    expect(query.mock.calls.some(([text]) => String(text).includes('ordersAggregate'))).toBe(true);
  });

  it('exports every fetched page to CSV, not only the visible first page', async () => {
    const query = vi.fn(async (_text: string, variables?: Record<string, unknown>) => {
      const offset = variables?.offset as number;
      return { orders: { total: 3, data: offset === 0 ? [
        { region: 'east', owner: 'a', amount: 1 }, { region: 'east', owner: 'b', amount: 2 },
      ] : [{ region: 'west', owner: 'c', amount: 3 }] } };
    });
    const csv = await buildReportCsv({ query } as GraphQLFetcher, definition, 2);
    expect(csv.split('\r\n')).toEqual(['region,owner,amount', 'east,a,1', 'east,b,2', 'west,c,3']);
  });
});
