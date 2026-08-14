import { describe, it, expect } from 'vitest';
import type { ColumnDef } from '@tanstack/react-table';
import { orderColumns } from './column-order';

type Row = Record<string, unknown>;

const cols = (...ids: string[]): ColumnDef<Row, unknown>[] =>
    ids.map((id) => ({ id, accessorKey: id }));

const idsOf = (columns: ColumnDef<Row, unknown>[]) => columns.map((c) => c.id);

describe('orderColumns', () => {
    it('moves the trailing columns to the end, in the order named', () => {
        const ordered = orderColumns(cols('id', 'created', 'name', 'modified'), {
            trailing: ['created', 'modified'],
        });
        expect(idsOf(ordered)).toEqual(['id', 'name', 'created', 'modified']);
    });

    it('puts the leading columns first, in the order named', () => {
        const ordered = orderColumns(cols('id', 'number', 'location', 'startDate', 'status'), {
            leading: ['id', 'number', 'location', 'status', 'startDate'],
        });
        expect(idsOf(ordered)).toEqual(['id', 'number', 'location', 'status', 'startDate']);
    });

    it('keeps unnamed columns in schema order between the two ends', () => {
        const ordered = orderColumns(cols('created', 'id', 'reviewer', 'frequency', 'modified'), {
            leading: ['id'],
            trailing: ['created', 'modified'],
        });
        expect(idsOf(ordered)).toEqual(['id', 'reviewer', 'frequency', 'created', 'modified']);
    });

    it('skips names the table does not have', () => {
        const ordered = orderColumns(cols('id', 'created'), { trailing: ['created', 'modified'] });
        expect(idsOf(ordered)).toEqual(['id', 'created']);
    });

    it('returns the columns untouched when nothing is named', () => {
        const original = cols('id', 'name');
        expect(orderColumns(original, {})).toBe(original);
        expect(orderColumns(original, { leading: [], trailing: [] })).toBe(original);
        expect(orderColumns(original, { trailing: ['absent'] })).toBe(original);
    });
});
