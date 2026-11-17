import type { Column, RowData } from '@tanstack/react-table';
import type { DataTableFeatures } from '@/lib/table-features';
import { useQuery } from '@tanstack/react-query';
import { X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from '@/components/ui/select';
import { useFetcher } from '@/common/fetcher';
import { useSchema } from '@/hooks/useSchema';
import { coerceForGql, gqlTypeOf } from '@/lib/fk';
import { FK_TUPLE_OPERATOR, isFkTupleValue, isGraphQlName, type FkTupleValue } from '@/lib/query-builder';
import type { ColumnFilterValue } from '@/hooks/useDataTable';
import type { Column as SchemaColumn } from '@/types/schema';

interface LookupRow {
    id: number | string;
    label: string;
}

interface LookupResponse {
    [tableName: string]: {
        data: LookupRow[];
    };
}

interface FkFilterProps<TData extends RowData, TValue> {
    column: Column<DataTableFeatures, TData, TValue>;
    joinTable: string;
    joinLabelColumn: string;
    /**
     * The destination column of the join (FK target). Used to project the option values
     * so the filter applies against the same column the FK points at. Do NOT substitute
     * the target table's first primary-key column here — on composite-PK or denormalized-FK
     * targets those are different columns.
     */
    joinFkColumn: string;
}

export function buildFkLookupQuery(
    joinTable: string,
    joinLabelColumn: string,
    joinFkColumn: string,
    columns: readonly Pick<SchemaColumn, 'name'>[] | undefined,
): string | null {
    if (!isGraphQlName(joinTable) || !isGraphQlName(joinLabelColumn) || !isGraphQlName(joinFkColumn)) {
        return null;
    }
    if (columns && (!columns.some((c) => c.name === joinLabelColumn) || !columns.some((c) => c.name === joinFkColumn))) {
        return null;
    }
    return `query Lookup${joinTable} { ${joinTable}(sort: [${joinLabelColumn}_asc], limit: 100) { data { id: ${joinFkColumn} label: ${joinLabelColumn} } } }`;
}

export function FkFilter<TData extends RowData, TValue>({ column, joinTable, joinLabelColumn, joinFkColumn }: FkFilterProps<TData, TValue>) {
    const fetcher = useFetcher();
    const schema = useSchema();
    const joinSchema = schema.findTable(joinTable);

    const query = buildFkLookupQuery(joinTable, joinLabelColumn, joinFkColumn, joinSchema?.columns);

    // Value coercion below follows the FK target column's declared GraphQL type,
    // not what the string looks like (a string key like "0123" must stay a string;
    // a Boolean FK must be sent as a boolean, not "true").
    const fkGqlType = gqlTypeOf(joinSchema?.columns.find((c) => c.name === joinFkColumn));

    const { data: lookupData } = useQuery({
        // joinFkColumn is part of the projection (`id: ${joinFkColumn}`), so two
        // joins to the same table on different columns must not share a cache entry.
        queryKey: ['fkLookup', joinTable, joinLabelColumn, joinFkColumn],
        queryFn: () => fetcher.query<LookupResponse>(query!),
        staleTime: 5 * 60 * 1000,
        enabled: !!joinSchema && !!query,
    });

    const options = lookupData?.[joinTable]?.data ?? [];
    const currentFilter = column.getFilterValue() as ColumnFilterValue | undefined;

    const selectedValue = currentFilter?.operator === '_eq' && currentFilter.value != null
        ? String(currentFilter.value)
        : 'all';

    const handleChange = (value: string) => {
        if (value === 'all') {
            column.setFilterValue(undefined);
            return;
        }
        column.setFilterValue({ operator: '_eq', value: coerceForGql(value, fkGqlType) } as ColumnFilterValue);
    };

    const handleClear = () => {
        column.setFilterValue(undefined);
    };

    return (
        <div className="flex flex-col gap-2 p-2 min-w-[220px]">
            <Select value={selectedValue} onValueChange={handleChange}>
                <SelectTrigger size="sm" className="w-full text-xs">
                    <SelectValue />
                </SelectTrigger>
                <SelectContent>
                    <SelectItem value="all">All</SelectItem>
                    {options.map((opt) => (
                        <SelectItem key={String(opt.id)} value={String(opt.id)}>
                            {String(opt.label)}
                        </SelectItem>
                    ))}
                </SelectContent>
            </Select>
            {(currentFilter !== undefined) && (
                <Button
                    variant="ghost"
                    size="sm"
                    className="h-7 text-xs justify-start"
                    onClick={handleClear}
                >
                    <X className="size-3" />
                    Clear filter
                </Button>
            )}
        </div>
    );
}

interface TupleLookupResponse {
    [tableName: string]: {
        data: Record<string, unknown>[];
    };
}

interface FkTupleFilterProps<TData extends RowData, TValue> {
    /** The FK's anchor column: the tuple filter is stored on it. */
    column: Column<DataTableFeatures, TData, TValue>;
    joinTable: string;
    joinLabelColumn: string;
    /** The FK's source (child) columns, in FK order. */
    sourceColumns: readonly string[];
    /** The FK's destination (parent) columns, paired positionally with sourceColumns. */
    destinationColumns: readonly string[];
}

/**
 * Lookup query for a composite-FK tuple filter: the parent rows' label plus
 * every destination column, aliased `k0..kN` so the projection never depends on
 * a column name being a usable alias. Null when any name is not a GraphQL name,
 * the arity is unpaired, or the parent schema lacks a named column.
 */
export function buildFkTupleLookupQuery(
    joinTable: string,
    joinLabelColumn: string,
    destinationColumns: readonly string[],
    columns: readonly Pick<SchemaColumn, 'name'>[] | undefined,
): string | null {
    if (!isGraphQlName(joinTable) || !isGraphQlName(joinLabelColumn)) return null;
    if (destinationColumns.length === 0 || !destinationColumns.every(isGraphQlName)) return null;
    if (columns && ![joinLabelColumn, ...destinationColumns].every((name) => columns.some((c) => c.name === name))) return null;
    const keys = destinationColumns.map((name, k) => `k${k}: ${name}`).join(' ');
    return `query LookupTuple${joinTable} { ${joinTable}(sort: [${joinLabelColumn}_asc], limit: 100) { data { label: ${joinLabelColumn} ${keys} } } }`;
}

/**
 * The tuple filter value selecting `parentRow`: one `[sourceColumn, value]`
 * pair per FK column, values as the server sent them. Null when the row lacks a
 * key part or holds NULL in one: an FK tuple with a NULL part references no row,
 * and `_eq null` would not match it anyway.
 */
export function fkTupleFor(parentRow: Record<string, unknown>, sourceColumns: readonly string[]): FkTupleValue | null {
    const pairs: FkTupleValue = [];
    for (let k = 0; k < sourceColumns.length; k++) {
        const part = parentRow[`k${k}`];
        if (typeof part !== 'string' && typeof part !== 'number' && typeof part !== 'boolean') return null;
        pairs.push([sourceColumns[k], part]);
    }
    return pairs;
}

/** Stable option key of a tuple: its values as strings, in FK order. */
function tupleKey(tuple: FkTupleValue): string {
    return JSON.stringify(tuple.map(([, part]) => String(part)));
}

/**
 * Header filter for a composite FK: pick ONE parent row and filter the child on
 * all of its FK columns at once. Filtering a single member column (the old
 * fallback) matched every parent that shared that column's value.
 */
export function FkTupleFilter<TData extends RowData, TValue>({ column, joinTable, joinLabelColumn, sourceColumns, destinationColumns }: FkTupleFilterProps<TData, TValue>) {
    const fetcher = useFetcher();
    const schema = useSchema();
    const joinSchema = schema.findTable(joinTable);
    const paired = sourceColumns.length === destinationColumns.length;
    const query = paired ? buildFkTupleLookupQuery(joinTable, joinLabelColumn, destinationColumns, joinSchema?.columns) : null;

    const { data: lookupData } = useQuery({
        queryKey: ['fkTupleLookup', joinTable, joinLabelColumn, ...destinationColumns],
        queryFn: () => fetcher.query<TupleLookupResponse>(query!),
        staleTime: 5 * 60 * 1000,
        enabled: !!joinSchema && !!query,
    });

    const options = (lookupData?.[joinTable]?.data ?? []).flatMap((row) => {
        const tuple = fkTupleFor(row, sourceColumns);
        return tuple ? [{ key: tupleKey(tuple), label: String(row.label ?? ''), tuple }] : [];
    });
    const currentFilter = column.getFilterValue() as ColumnFilterValue | undefined;
    const current = currentFilter?.operator === FK_TUPLE_OPERATOR && isFkTupleValue(currentFilter.value) ? currentFilter.value : null;
    const selectedValue = current ? tupleKey(current) : 'all';

    const handleChange = (value: string) => {
        if (value === 'all') {
            column.setFilterValue(undefined);
            return;
        }
        const option = options.find((o) => o.key === value);
        if (option) column.setFilterValue({ operator: FK_TUPLE_OPERATOR, value: option.tuple } as ColumnFilterValue);
    };

    if (!query) {
        return <p role="note" className="p-2 text-xs text-muted-foreground">This foreign key can't be filtered: its columns don't resolve on {joinTable}.</p>;
    }

    return (
        <div className="flex flex-col gap-2 p-2 min-w-[220px]">
            <Select value={selectedValue} onValueChange={handleChange}>
                <SelectTrigger size="sm" className="w-full text-xs" aria-label={`Filter by ${joinTable}`}>
                    <SelectValue />
                </SelectTrigger>
                <SelectContent>
                    <SelectItem value="all">All</SelectItem>
                    {current && !options.some((o) => o.key === selectedValue) && (
                        // A tuple from the URL whose parent is outside the first 100 options.
                        <SelectItem value={selectedValue}>{current.map(([, part]) => String(part)).join(' · ')}</SelectItem>
                    )}
                    {options.map((opt) => (
                        <SelectItem key={opt.key} value={opt.key}>
                            {opt.label}
                        </SelectItem>
                    ))}
                </SelectContent>
            </Select>
            {currentFilter !== undefined && (
                <Button
                    variant="ghost"
                    size="sm"
                    className="h-7 text-xs justify-start"
                    onClick={() => column.setFilterValue(undefined)}
                >
                    <X className="size-3" />
                    Clear filter
                </Button>
            )}
        </div>
    );
}
