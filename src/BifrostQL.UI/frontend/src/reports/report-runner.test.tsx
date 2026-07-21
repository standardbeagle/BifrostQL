// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { buildReportCsv, ReportRunner, runReport } from './report-runner';
import type { GraphQLFetcher } from '@standardbeagle/edit-db';

const definition = {
  source: { kind: 'table' as const, table: 'orders' },
  columns: [{ column: 'region' }, { column: 'owner' }, { column: 'amount' }],
  groupBands: [
    { column: 'region', sortDir: 'asc' as const, totals: [{ column: 'amount', op: 'sum' as const }] },
    { column: 'owner', sortDir: 'desc' as const, totals: [{ column: 'amount', op: 'sum' as const }] },
  ],
  grandTotals: [{ column: 'amount', op: 'sum' as const }],
  pageHeader: { title: 'Orders' },
};

afterEach(cleanup);

describe('runReport', () => {
  it('uses grouped aggregate results for two-level subtotal and grand totals, never page-row sums', async () => {
    const query = vi.fn(async (text: string, variables?: Record<string, unknown>) => {
      if (text.includes('ordersAggregate')) {
        return {
          band0: [
            { region: 'east', _sum: { amount: 10 } },
            { region: 'west', _sum: { amount: 11 } },
          ],
          band1: [
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
    expect(result.bandTotals[0].get('east')?.['_sum.amount']).toBe(10);
    expect(result.grandTotals['_sum.amount']).toBe(21);
    expect(result.rows).toHaveLength(3);
    expect(query.mock.calls.some(([text]) => String(text).includes('ordersAggregate'))).toBe(true);
    const detailQuery = query.mock.calls.find(([text]) => !String(text).includes('ordersAggregate'))?.[0] as string;
    expect(detailQuery).toContain('sort: [region_asc, owner_desc]');
    expect(detailQuery).toContain('data { region owner amount }');
  });

  it('renders two-level group headers and aggregate-derived subtotals at each boundary', async () => {
    const query = vi.fn(async (text: string, variables?: Record<string, unknown>) => {
      if (text.includes('ordersAggregate')) return {
        band0: [{ region: 'east', _sum: { amount: 10 } }, { region: 'west', _sum: { amount: 11 } }],
        band1: [
          { region: 'east', owner: 'a', _sum: { amount: 7 } },
          { region: 'east', owner: 'b', _sum: { amount: 3 } },
          { region: 'west', owner: 'c', _sum: { amount: 11 } },
        ], grand: [{ _sum: { amount: 21 } }],
      };
      return { orders: { total: 3, data: variables?.offset === 0 ? [
        { region: 'east', owner: 'a', amount: 999 }, { region: 'east', owner: 'b', amount: 999 }, { region: 'west', owner: 'c', amount: 999 },
      ] : [] } };
    });
    render(<ReportRunner definition={definition} fetcher={{ query } as GraphQLFetcher} />);
    await waitFor(() => expect(screen.getByText('region: east subtotal _sum.amount: 10')).toBeTruthy());
    expect(screen.getByText('owner: a subtotal _sum.amount: 7')).toBeTruthy();
    expect(screen.getByText('owner: b subtotal _sum.amount: 3')).toBeTruthy();
    expect(screen.getByText('region: west subtotal _sum.amount: 11')).toBeTruthy();
    expect(screen.getByText('owner: c subtotal _sum.amount: 11')).toBeTruthy();
  });

  it('requests band ordering so an interleaved source cannot repeat a group header or subtotal', async () => {
    const query = vi.fn(async (text: string) => {
      if (text.includes('ordersAggregate')) return {
        band0: [{ region: 'east', _sum: { amount: 10 } }, { region: 'west', _sum: { amount: 11 } }],
        band1: [{ region: 'east', owner: 'b', _sum: { amount: 3 } }, { region: 'east', owner: 'a', _sum: { amount: 7 } }, { region: 'west', owner: 'c', _sum: { amount: 11 } }],
        grand: [{ _sum: { amount: 21 } }],
      };
      const interleaved = [
        { region: 'east', owner: 'b', amount: 999 }, { region: 'west', owner: 'c', amount: 999 }, { region: 'east', owner: 'a', amount: 999 },
      ];
      // The fake server returns a stable group order only when the runner asks
      // for it; this guards the detail-query contract as well as the rendered bands.
      const ordered = text.includes('sort: [region_asc, owner_desc]')
        ? [interleaved[0], interleaved[2], interleaved[1]] : interleaved;
      return { orders: { total: ordered.length, data: ordered } };
    });
    render(<ReportRunner definition={definition} fetcher={{ query } as GraphQLFetcher} />);
    await waitFor(() => expect(screen.getAllByText('region: east')).toHaveLength(1));
    expect(screen.getAllByText('region: east subtotal _sum.amount: 10')).toHaveLength(1);
    expect(document.querySelectorAll('.bifrost-report__band')).toHaveLength(5);
    expect(document.querySelectorAll('.bifrost-report__subtotal')).toHaveLength(5);
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
