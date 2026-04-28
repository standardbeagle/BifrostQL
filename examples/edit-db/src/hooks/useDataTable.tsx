import { useQuery } from "@tanstack/react-query";
import { Link, useNavigate, useSearchParams } from "./usePath";
import { useCallback, useMemo, useRef, useState } from "react";
import { useSchema } from "./useSchema";
import { Table, Column, Join, Schema } from "../types/schema";
import { ColumnDef, ColumnFiltersState, SortingState } from "@tanstack/react-table";
import { useFetcher } from "../common/fetcher";
import { DataTableColumnHeader } from "../components/data-table-column-header";
import { FkCellPopover } from "../components/fk-cell-popover";
import { PanelRight, List } from "lucide-react";
import type { ColumnPanel } from "../data-panel";
import { Button } from "../components/ui/button";
import { HoverCard, HoverCardTrigger, HoverCardContent } from "../components/ui/hover-card";
import { ContentViewer } from "../components/content-viewer";
import { isLongTextDbType, isBinaryDbType } from "../lib/content-detect";
import {
    getFilterOperators,
    getFilterObj,
    toLocaleDate,
    getRowPkValue,
    getGraphQlType,
    getPkTypes,
    buildColumnFilters,
    serializeColumnFilters,
    deserializeColumnFilters,
    buildQuery,
    buildPkEqVariables,
} from "../lib/query-builder";

// Re-export for existing filter component imports.
export { getFilterOperators } from "../lib/query-builder";
export type { ColumnFilterValue } from "../lib/query-builder";

const numericTypes = ["Int", "Int!", "Float", "Float!"];

interface RowData {
    id?: number | string;
    [key: string]: unknown;
}

interface ColumnWithJoin extends Column {
    joinTable?: Join;
    joinLabelColumn?: string;
}

// Joined rows in GraphQL responses are always aliased as `{ id: destCol }` by the SDL query
// builder, so the joined-row PK lookup is distinct from the source-row composite PK lookup.
const getJoinedRowPkValue = (row: RowData): string => String(row?.id ?? "");

const getTableColumns = (table: Table, schema: Schema, onExpandContent?: (rowIndex: number, columnName: string) => void, onOpenColumn?: (panel: ColumnPanel) => void): ColumnDef<RowData, unknown>[] => {
    if (!table || !schema) return [];

    const dataColumns: ColumnDef<RowData, unknown>[] = table.columns
        .map((c: Column): ColumnDef<RowData, unknown> => {
            const singleJoin = table.singleJoins.find((j: Join) => j.sourceColumnNames?.[0] === c.name);
            const operators = getFilterOperators(c.paramType);

            if (singleJoin) {
                const columnName = singleJoin.destinationTable;
                const joinSchema = schema.findTable(singleJoin.destinationTable);
                const joinLabelColumn = joinSchema?.labelColumn ?? 'id';
                // Self-referential FK: column points back at the same table
                // (e.g. categories.parent_id -> categories.id). Label it clearly
                // so users know the FK means "parent" rather than an unrelated
                // join.
                const isSelfReference = singleJoin.destinationTable === table.name;
                const headerTitle = isSelfReference ? `Parent ${c.label}` : c.label;
                return {
                    id: c.name,
                    accessorKey: c.name,
                    header: ({ column, table: t }) => <DataTableColumnHeader column={column} table={t} title={headerTitle} />,
                    enableSorting: true,
                    meta: { sortField: c.name, paramType: c.paramType, filterOperators: operators, joinTable: singleJoin.destinationTable, joinLabelColumn, joinFkColumn: singleJoin.destinationColumnNames[0], column: c, isSelfReference },
                    cell: ({ row }) => {
                        const joined = row.original[columnName] as RowData | undefined;
                        if (!joined) return null;
                        const joinedPk = getJoinedRowPkValue(joined);
                        return (
                            <span className="group/fk inline-flex items-center gap-0.5">
                                <FkCellPopover
                                    tableName={singleJoin.destinationTable}
                                    recordId={joinedPk}
                                    filterColumn={singleJoin.destinationColumnNames[0]}
                                >
                                    <Link to={"/" + joinSchema?.name + "/" + joinedPk} className="text-primary hover:text-primary/80 hover:underline">
                                        {joined?.label as string}
                                    </Link>
                                </FkCellPopover>
                                {onOpenColumn && (
                                    <Button
                                        variant="ghost"
                                        size="icon-sm"
                                        className="opacity-0 group-hover/fk:opacity-100 size-5 shrink-0"
                                        onClick={(e) => {
                                            e.stopPropagation();
                                            onOpenColumn({
                                                tableName: singleJoin.destinationTable,
                                                filterId: joinedPk,
                                            });
                                        }}
                                        aria-label="Open in side column"
                                        title="Open in side column"
                                    >
                                        <PanelRight className="size-3" />
                                    </Button>
                                )}
                            </span>
                        );
                    },
                };
            }

            if ((c as ColumnWithJoin)?.joinTable) {
                return {
                    id: c.name,
                    accessorKey: c.name,
                    header: ({ column, table: t }) => <DataTableColumnHeader column={column} table={t} title={c.label} />,
                    enableSorting: true,
                    meta: { sortField: c.name, paramType: c.paramType, filterOperators: operators, column: c },
                    cell: ({ row }) => {
                        const joined = row.original[c.name] as RowData | undefined;
                        if (!joined) return null;
                        return (
                            <Link to={"/" + c.name + "/" + getJoinedRowPkValue(joined)} className="text-primary hover:text-primary/80 hover:underline">
                                {c.name}
                            </Link>
                        );
                    },
                };
            }

            if (c?.paramType === "DateTime") {
                return {
                    id: c.name,
                    accessorFn: (row) => toLocaleDate(row?.[c.name] as string),
                    header: ({ column, table: t }) => <DataTableColumnHeader column={column} table={t} title={c.label} />,
                    enableSorting: true,
                    meta: { sortField: c.name, paramType: c.paramType, dbType: c.dbType, filterOperators: operators, column: c },
                };
            }

            const useContentViewer = isLongTextDbType(c.dbType) || isBinaryDbType(c.dbType);

            if (useContentViewer) {
                return {
                    id: c.name,
                    accessorFn: (row) => (c.name ? String(row?.[c.name] ?? "") : ""),
                    header: ({ column, table: t }) => <DataTableColumnHeader column={column} table={t} title={c.label} />,
                    enableSorting: true,
                    meta: { sortField: c.name, paramType: c.paramType, dbType: c.dbType, filterOperators: operators, column: c },
                    cell: ({ row }) => (
                        <ContentViewer
                            value={row.original[c.name]}
                            dbType={c.dbType}
                            onExpand={onExpandContent ? () => onExpandContent(row.index, c.name) : undefined}
                        />
                    ),
                };
            }

            return {
                id: c.name,
                accessorFn: (row) => (c.name ? String(row?.[c.name] ?? "") : ""),
                header: ({ column, table: t }) => <DataTableColumnHeader column={column} table={t} title={c.label} />,
                enableSorting: true,
                meta: { sortField: c.name, paramType: c.paramType, dbType: c.dbType, filterOperators: operators, column: c },
            };
        });

    const multiJoinColumns: ColumnDef<RowData, unknown>[] = table.multiJoins
        .map((j: Join): ColumnDef<RowData, unknown> => {
            const joinTable = schema.findTable(j.destinationTable);
            const labelCol = joinTable?.labelColumn ?? 'id';
            // Self-referential multi-join (e.g. child categories pointing back
            // at the same categories table) — prefix with "Child" so the user
            // can tell it apart from the "Parent" single-join column.
            const isSelfReference = j.destinationTable === table.name;
            const headerLabel = isSelfReference
                ? `Child ${joinTable?.label ?? j.destinationTable}`
                : joinTable?.label ?? j.destinationTable;
            return {
                id: `join_${j.destinationTable}`,
                header: headerLabel,
                enableSorting: false,
                enableHiding: true,
                cell: ({ row }) => {
                    const parentPk = getRowPkValue(row.original, table);
                    const children = (row.original[j.destinationTable] as RowData[] | undefined) ?? [];
                    const count = children.length;
                    if (count === 0) {
                        return <span className="text-muted-foreground">—</span>;
                    }
                    const titles = children.slice(0, 10).map(c => String(c[labelCol] ?? '')).filter(Boolean);
                    const joinLabel = joinTable?.label ?? j.destinationTable;
                    return (
                        <span className="group/fk inline-flex items-center gap-1">
                            <HoverCard openDelay={300} closeDelay={100}>
                                <HoverCardTrigger asChild>
                                    <Link
                                        to={"/" + joinTable?.name + "/from/" + table.name + "/" + parentPk}
                                        className="inline-flex items-center gap-1 rounded-full border border-border bg-muted/40 px-2 py-0.5 text-xs font-medium text-primary hover:bg-muted hover:border-primary/40 transition-colors"
                                    >
                                        <List className="size-3" />
                                        {count}
                                    </Link>
                                </HoverCardTrigger>
                                <HoverCardContent align="start" sideOffset={6} className="w-auto max-w-xs p-3">
                                    <div className="flex items-center gap-2 border-b pb-2 mb-2">
                                        <List className="size-3.5 text-primary" />
                                        <span className="text-xs font-semibold text-foreground">
                                            {count} {joinLabel}
                                        </span>
                                    </div>
                                    <ul className="space-y-0.5 text-xs text-foreground">
                                        {titles.map((t, i) => (
                                            <li key={i} className="truncate">{t}</li>
                                        ))}
                                        {count > 10 && (
                                            <li className="text-muted-foreground italic">… and {count - 10} more</li>
                                        )}
                                    </ul>
                                </HoverCardContent>
                            </HoverCard>
                            {onOpenColumn && (
                                <Button
                                    variant="ghost"
                                    size="icon-sm"
                                    className="opacity-40 group-hover/fk:opacity-100 size-5 shrink-0 transition-opacity"
                                    onClick={(e) => {
                                        e.stopPropagation();
                                        onOpenColumn({
                                            tableName: j.destinationTable,
                                            filterTable: table.name,
                                            filterId: parentPk,
                                            filterColumn: j.destinationColumnNames[0],
                                        });
                                    }}
                                    aria-label="Open in side column"
                                    title="Open in side column"
                                >
                                    <PanelRight className="size-3" />
                                </Button>
                            )}
                        </span>
                    );
                },
            };
        });

    return [...dataColumns, ...multiJoinColumns];
}

/**
 * Build the id-lookup variables dict that matches the query shape emitted by `buildQuery`
 * for the current table + filter context:
 *
 * - By own PK (composite-aware): `{id: ...}` for single PK, `{pk_${col}: ...}` per column.
 * - FK column path: `{id: coerced-to-fk-type}`.
 * - Nested parent filter (single parent PK): `{id: coerced-to-parent-pk-type}`.
 * - Nested parent filter (composite parent PK): `{pk_${col}: ...}` per parent PK column.
 */
function buildIdLookupVariables(
    id: string,
    table: Table,
    schema: Schema,
    filterTable?: string,
    filterColumn?: string,
): Record<string, unknown> {
    if (!filterTable && !filterColumn) {
        return buildPkEqVariables(id, table);
    }

    const tableSchema = schema.findTable(table.graphQlName) ?? table;
    const fkColumn = filterColumn
        ?? tableSchema.singleJoins.find((j: Join) => j.destinationTable === filterTable)?.sourceColumnNames?.[0];

    if (fkColumn) {
        const fkCol = table.columns.find((c) => c.name === fkColumn);
        const coerced = fkCol && numericTypes.includes(fkCol.paramType) ? Number(id) : id;
        return { id: coerced };
    }

    const parentTable = schema.findTable(filterTable!);
    if (parentTable && (parentTable.primaryKeys?.length ?? 0) > 1) {
        return buildPkEqVariables(id, parentTable);
    }
    const parentPkType = parentTable ? getPkTypes(parentTable)[0]?.gqlType : undefined;
    const isNumeric = parentPkType ? numericTypes.includes(parentPkType) : true;
    return { id: isNumeric ? Number(id) : id };
}

interface TableQueryData {
    data: RowData[];
    total: number;
    offset: number;
    limit: number;
}

interface QueryData {
    [tableName: string]: TableQueryData;
}

/**
 * Return type for the useDataTable hook.
 * @interface UseDataTableResult
 */
interface UseDataTableResult {
    /** Column definitions for the table */
    columns: ColumnDef<RowData, unknown>[];
    /** Current sorting state */
    sorting: SortingState;
    /** Active column filters */
    columnFilters: ColumnFiltersState;
    /** Primary-key column names in declaration order (empty when the table has no PK). */
    primaryKeys: string[];
    /** Current page index (0-based) */
    pageIndex: number;
    /** Number of rows per page */
    pageSize: number;
    /** Total number of pages */
    pageCount: number;
    /** Current page data rows */
    rows: RowData[];
    /** Whether data is currently loading */
    loading: boolean;
    /** Error object if the query failed */
    error: Error | null;
    /** Update sorting state */
    onSortingChange: (sorting: SortingState) => void;
    /** Update column filters */
    onColumnFiltersChange: (filters: ColumnFiltersState) => void;
    /** Update page index */
    onPageIndexChange: (pageIndex: number) => void;
    /** Update page size */
    onPageSizeChange: (pageSize: number) => void;
}

/**
 * Hook for managing data table state including sorting, filtering, and pagination.
 * 
 * Automatically builds GraphQL queries based on table schema and current state,
 * handles data fetching via React Query, and provides column definitions with
 * proper rendering for foreign keys, dates, and content fields.
 * 
 * @example
 * ```tsx
 * const {
 *   columns,
 *   rows,
 *   loading,
 *   pageIndex,
 *   pageCount,
 *   onSortingChange,
 *   onPageIndexChange,
 * } = useDataTable(table, recordId, parentTable);
 * ```
 * 
 * @param table - Table schema definition
 * @param id - Optional record ID for filtering to a specific row
 * @param filterTable - Optional parent table name for relationship filtering
 * @param filterColumn - Optional column name for explicit FK filtering
 * @param onExpandContent - Callback when content expansion is requested
 * @param onOpenColumn - Callback when opening a side panel column
 * @returns Data table state and control functions
 */
export function useDataTable(table: Table | null, id?: string, filterTable?: string, filterColumn?: string, onExpandContent?: (rowIndex: number, columnName: string) => void, onOpenColumn?: (panel: ColumnPanel) => void): UseDataTableResult {
    const { search } = useSearchParams();
    const navigate = useNavigate();
    const filterString = search.get('filter') ?? '';
    const cfParam = search.get('cf') ?? '';
    const { variables: filterVariables } = getFilterObj(filterString);
    const schema = useSchema();
    const fetcher = useFetcher();

    const [sorting, setSorting] = useState<SortingState>([]);
    const [columnFilters, setColumnFilters] = useState<ColumnFiltersState>(() => deserializeColumnFilters(cfParam));
    const [pageIndex, setPageIndex] = useState(0);
    const [pageSize, setPageSize] = useState(50);

    // Reset sort, filters, and pagination when the table changes
    const tableRef = useRef(table);
    if (table && table !== tableRef.current) {
        tableRef.current = table;
        if (sorting.length > 0) setSorting([]);
        if (columnFilters.length > 0) setColumnFilters([]);
        if (pageIndex !== 0) setPageIndex(0);
    }

    const appliedSort = useMemo(() => {
        if (sorting.length > 0 && table) {
            const col = sorting[0];
            // Validate that the sort column exists on the current table
            const columnExists = table.columns.some((c) => c.name === col.id);
            if (columnExists) {
                return [`${col.id}_${col.desc ? 'desc' : 'asc'}`];
            }
        }
        return table ? [`${table.columns.at(0)?.name ?? 'id'}_asc`] : [];
    }, [sorting, table]);

    const query = useMemo(
        () => buildQuery(table!, schema, filterString, columnFilters, id, filterTable, filterColumn),
        [table, schema, filterString, columnFilters, id, filterTable, filterColumn]
    );

    const cfVariables = useMemo(
        () => table ? buildColumnFilters(columnFilters, table).variables : {},
        [columnFilters, table]
    );

    const offset = pageIndex * pageSize;

    const queryVariables = useMemo(() => ({
        sort: appliedSort,
        limit: pageSize,
        offset,
        ...(id && table ? buildIdLookupVariables(id, table, schema, filterTable, filterColumn) : {}),
        ...filterVariables,
        ...cfVariables,
    }), [appliedSort, pageSize, offset, id, table, schema, filterTable, filterColumn, filterVariables, cfVariables]);

    const { isLoading, error, data } = useQuery({
        queryKey: ['tableData', table?.name, query, queryVariables],
        queryFn: () => fetcher.query<QueryData>(query!, queryVariables),
        enabled: !!query && !!table && appliedSort.length > 0,
    });

    const columns = useMemo(
        () => table ? getTableColumns(table, schema, onExpandContent, onOpenColumn) : [],
        [table, schema, onExpandContent, onOpenColumn]
    );

    const tableData = data?.[table?.name ?? ''];
    const rows = tableData?.data ?? [];
    const totalRows = tableData?.total ?? 0;
    const pageCount = Math.max(1, Math.ceil(totalRows / pageSize));

    const syncFiltersToUrl = useCallback((filters: ColumnFiltersState) => {
        const params = new URLSearchParams();
        const currentFilter = search.get('filter');
        if (currentFilter) params.set('filter', currentFilter);
        const serialized = serializeColumnFilters(filters);
        if (serialized) params.set('cf', serialized);
        const qs = params.toString();
        navigate(qs ? `?${qs}` : '?');
    }, [search, navigate]);

    const onSortingChange = useCallback((newSorting: SortingState) => {
        setSorting(newSorting);
        setPageIndex(0);
    }, []);

    const onColumnFiltersChange = useCallback((newFilters: ColumnFiltersState) => {
        setColumnFilters(newFilters);
        setPageIndex(0);
        syncFiltersToUrl(newFilters);
    }, [syncFiltersToUrl]);

    const onPageIndexChange = useCallback((newPageIndex: number) => {
        setPageIndex(newPageIndex);
    }, []);

    const onPageSizeChange = useCallback((newPageSize: number) => {
        setPageSize(newPageSize);
        setPageIndex(0);
    }, []);

    const primaryKeys = table?.primaryKeys ?? [];

    return {
        columns,
        sorting,
        columnFilters,
        primaryKeys,
        pageIndex,
        pageSize,
        pageCount,
        rows,
        loading: isLoading,
        error: error as Error | null,
        onSortingChange,
        onColumnFiltersChange,
        onPageIndexChange,
        onPageSizeChange,
    };
}
