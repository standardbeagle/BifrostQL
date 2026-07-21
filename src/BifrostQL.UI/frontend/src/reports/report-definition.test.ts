import { describe, expect, it } from 'vitest';
import { parseReportDefinition } from './report-definition';

describe('parseReportDefinition', () => {
  it('accepts a two-level grouped report definition', () => {
    expect(parseReportDefinition({
      source: { kind: 'table', table: 'orders' },
      columns: [{ column: 'region' }, { column: 'owner' }, { column: 'amount' }],
      groupBands: [
        { column: 'region', totals: [{ column: 'amount', op: 'sum' }] },
        { column: 'owner', totals: [{ column: 'amount', op: 'sum' }] },
      ],
      grandTotals: [{ column: 'amount', op: 'sum' }],
      pageHeader: { title: 'Orders' },
    })).not.toBeNull();
  });

  it('rejects unsafe GraphQL names before they can be interpolated into a query', () => {
    expect(parseReportDefinition({ source: { kind: 'table', table: 'orders { data' }, columns: [] })).toBeNull();
  });
});
