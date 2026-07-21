import { describe, expect, it, vi } from 'vitest';
import type { SavedObjectsClient } from '@standardbeagle/edit-db';
import { openReport, saveReport } from './report-store';

const definition = { source: { kind: 'table' as const, table: 'orders' }, columns: [{ column: 'id' }] };

describe('report saved objects', () => {
  it('round-trips an identical definition as type report', async () => {
    const put = vi.fn(async (object) => object);
    const saved = await saveReport({ put } as unknown as SavedObjectsClient, { id: 'r1', name: 'Orders', definition, version: 0 });
    expect(put).toHaveBeenCalledWith(expect.objectContaining({ type: 'report' }));
    expect(openReport(saved)).toEqual(definition);
  });
});
