import type { ColumnDef } from '@tanstack/react-table';

function columnId<TData>(column: ColumnDef<TData, unknown>): string | undefined {
    return column.id ?? ('accessorKey' in column ? String(column.accessorKey) : undefined);
}

function pick<TData>(columns: ColumnDef<TData, unknown>[], names: string[]): ColumnDef<TData, unknown>[] {
    return names
        .map((name) => columns.find((c) => columnId(c) === name))
        .filter((c): c is ColumnDef<TData, unknown> => c !== undefined);
}

export interface ColumnOrderConfig {
    /** Columns to place first, in this order. */
    leading?: string[];
    /** Columns to place last, in this order. */
    trailing?: string[];
}

/**
 * Reorder columns around a host's preferences: named leading columns first in the
 * order named, named trailing columns last in the order named, everything else
 * keeping its schema order in between. Names the table doesn't have are skipped,
 * so one host config can cover tables that don't all carry the same columns.
 *
 * Order is independent of visibility: a column the viewer switches back on lands
 * in its configured place rather than at the end.
 */
export function orderColumns<TData>(
    columns: ColumnDef<TData, unknown>[],
    { leading, trailing }: ColumnOrderConfig,
): ColumnDef<TData, unknown>[] {
    const leadingCols = leading ? pick(columns, leading) : [];
    const trailingCols = trailing ? pick(columns, trailing) : [];
    if (leadingCols.length === 0 && trailingCols.length === 0) return columns;

    const moved = new Set([...leadingCols, ...trailingCols]);
    const middle = columns.filter((c) => !moved.has(c));
    return [...leadingCols, ...middle, ...trailingCols];
}
