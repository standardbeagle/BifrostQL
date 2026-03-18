import { useQuery } from "@tanstack/react-query";
import { Link, useNavigate, useSearchParams } from "./usePath";
import { useCallback, useMemo, useRef, useState } from "react";
import { useSchema } from "./useSchema";
import { Table, Column, Join, Schema } from "../types/schema";
import { ColumnDef, ColumnFiltersState, SortingState } from "@tanstack/react-table";
import { useFetcher } from "../common/fetcher";
import { DataTableColumnHeader } from "../components/data-table-column-header";
import { FkCellPopover } from "../components/fk-cell-popover";
import { PanelRight } from "lucide-react";
import type { ColumnPanel } from "../data-panel";
import { Button } from "../components/ui/button";
import { ContentViewer } from "../components/content-viewer";
import { isLongTextDbType, isBinaryDbType } from "../lib/content-detect";

const numericTypes = ["Int", "Int!", "Float", "Float!"];

interface FilterResult {
    variables: Record<string, unknown>;
    param: string;
    filterText: string;
}

interface RowData {
    id?: number | string;
    [key: string]: unknown;
}

interface ColumnWithJoin extends Column {
    joinTable?: Join;
    joinLabelColumn?: string;
}

export interface ColumnFilterValue {
    operator: string;
    value: unknown;
}

const columnFilterOperators: Record<string, string[]> = {
    String:   ["_eq", "_neq", "_contains", "_starts_with", "_ends_with", "_null"],
    Int:      ["_eq", "_neq", "_gt", "_gte", "_lt", "_lte", "_between", "_null"],
    Float:    ["_eq", "_neq", "_gt", "_gte", "_lt", "_lte", "_between", "_null"],
    Boolean:  ["_eq", "_null"],
    DateTime: ["_eq", "_neq", "_gt", "_gte", "_lt", "_lte", "_between", "_null"],
};

export function getFilterOperators(paramType: string): string[] {
    const baseType = paramType.replace("!", "");
    return columnFilterOperators[baseType] ?? columnFilterOperators.String;
}

const getFilterObj = (filterString: string): FilterResult => {
    try {
        if (!filterString) return { variables: {}, param: "", filterText: "" };
        const [column, action, value, type] = JSON.parse(filterString);
        return { variables: { filter: value }, param: `, $filter: ${type}`, filterText: `{${column}: {${action}: $filter} }` }
    } catch {
        return { variables: {}, param: "", filterText: "" };
    }
}

const toLocaleDate = (d: string): string => {
    if (!d) return "";
    const dd = new Date(d);
    if (dd.toString() === "Invalid Date") return "";
    if (dd < new Date('1973-01-01')) return "";
    return dd.toLocaleString();
};

const getRowPkValue = (row: RowData, table: Table): string => {
    const pk = table.primaryKeys?.[0];
    if (!pk) return String(row?.id ?? "");
    return String(row?.[pk] ?? "");
};

const getJoinedRowPkValue = (row: RowData): string => {
    return String(row?.id ?? "");
};

function getGraphQlType(paramType: string): string {
    const baseType = paramType.replace("!", "");
    switch (baseType) {
        case "Int": return "Int";
        case "Float": return "Float";
        case "Boolean": return "Boolean";
        case "DateTime": return "String";
        default: return "String";
    }
}

interface ColumnFilterResult {
    variables: Record<string, unknown>;
    params: string[];
    filterTexts: string[];
}

function buildColumnFilters(columnFilters: ColumnFiltersState, table: Table): ColumnFilterResult {
    const variables: Record<string, unknown> = {};
    const params: string[] = [];
    const filterTexts: string[] = [];

    for (const cf of columnFilters) {
        const filterValue = cf.value as ColumnFilterValue;
        if (filterValue.value === undefined || filterValue.value === null || filterValue.value === "") continue;

        const col = table.columns.find((c) => c.name === cf.id);
        if (!col) continue;

        const varName = `cf_${cf.id}`;
        const gqlType = getGraphQlType(col.paramType);

        if (filterValue.operator === "_null") {
            filterTexts.push(`{${cf.id}: {_null: ${filterValue.value ? "true" : "false"}}}`);
            continue;
        }

        if (filterValue.operator === "_between") {
            const range = filterValue.value as [unknown, unknown];
            if (!Array.isArray(range) || range.length !== 2) continue;
            const loVar = `${varName}_lo`;
            const hiVar = `${varName}_hi`;
            variables[loVar] = range[0];
            variables[hiVar] = range[1];
            params.push(`$${loVar}: ${gqlType}`, `$${hiVar}: ${gqlType}`);
            filterTexts.push(`{${cf.id}: {_between: [$${loVar}, $${hiVar}]}}`);
            continue;
        }

        variables[varName] = filterValue.value;
        params.push(`$${varName}: ${gqlType}`);
        filterTexts.push(`{${cf.id}: {${filterValue.operator}: $${varName}}}`);
    }

    return { variables, params, filterTexts };
}

function serializeColumnFilters(columnFilters: ColumnFiltersState): string {
    if (columnFilters.length === 0) return "";
    return JSON.stringify(columnFilters.map((cf) => [cf.id, (cf.value as ColumnFilterValue).operator, (cf.value as ColumnFilterValue).value]));
}

function deserializeColumnFilters(raw: string): ColumnFiltersState {
    try {
        if (!raw) return [];
        const parsed = JSON.parse(raw) as [string, string, unknown][];
        return parsed.map(([id, operator, value]) => ({ id, value: { operator, value } as ColumnFilterValue }));
    } catch {
        return [];
    }
}

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
                return {
                    id: c.name,
                    accessorKey: c.name,
                    header: ({ column, table: t }) => <DataTableColumnHeader column={column} table={t} title={c.label} />,
                    enableSorting: true,
                    meta: { sortField: c.name, paramType: c.paramType, filterOperators: operators, joinTable: singleJoin.destinationTable, joinLabelColumn },
                    cell: ({ row }) => {
                        const joined = row.original[columnName] as RowData | undefined;
                        if (!joined) return null;
                        const joinedPk = getJoinedRowPkValue(joined);
                        return (
                            <span className="group/fk inline-flex items-center gap-0.5">
                                <FkCellPopover tableName={singleJoin.destinationTable} recordId={joinedPk}>
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
                    meta: { sortField: c.name, paramType: c.paramType, filterOperators: operators },
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
                    meta: { sortField: c.name, paramType: c.paramType, dbType: c.dbType, filterOperators: operators },
                };
            }

            const useContentViewer = isLongTextDbType(c.dbType) || isBinaryDbType(c.dbType);

            if (useContentViewer) {
                return {
                    id: c.name,
                    accessorFn: (row) => (c.name ? String(row?.[c.name] ?? "") : ""),
                    header: ({ column, table: t }) => <DataTableColumnHeader column={column} table={t} title={c.label} />,
                    enableSorting: true,
                    meta: { sortField: c.name, paramType: c.paramType, dbType: c.dbType, filterOperators: operators },
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
                meta: { sortField: c.name, paramType: c.paramType, dbType: c.dbType, filterOperators: operators },
            };
        });

    const multiJoinColumns: ColumnDef<RowData, unknown>[] = table.multiJoins
        .map((j: Join): ColumnDef<RowData, unknown> => {
            const joinTable = schema.findTable(j.destinationTable);
            return {
                id: `join_${j.destinationTable}`,
                header: joinTable?.label ?? j.destinationTable,
                enableSorting: false,
                enableHiding: true,
                cell: ({ row }) => {
                    const parentPk = getRowPkValue(row.original, table);
                    return (
                        <span className="group/fk inline-flex items-center gap-0.5">
                            <Link to={"/" + joinTable?.name + "/from/" + table.name + "/" + parentPk} className="text-primary hover:text-primary/80 hover:underline">
                                {joinTable?.label ?? joinTable?.name}
                            </Link>
                            {onOpenColumn && (
                                <Button
                                    variant="ghost"
                                    size="icon-sm"
                                    className="opacity-0 group-hover/fk:opacity-100 size-5 shrink-0"
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

const getPkType = (table: Table): string => {
    const pkName = table.primaryKeys?.[0];
    if (!pkName) return "Int";
    const pkColumn = table.columns.find((c: Column) => c.name === pkName);
    return pkColumn?.paramType?.replace("!", "") ?? "Int";
};

const buildQuery = (
    table: Table,
    schema: Schema,
    filterString: string,
    columnFilters: ColumnFiltersState,
    id?: string,
    tableFilter?: string,
    filterColumn?: string,
): string | null => {
    if (!table || !schema?.data) return null;
    const tableSchema = schema.findTable(table.graphQlName);
    if (!tableSchema) return null;
    const primaryKey = tableSchema?.primaryKeys?.[0] ?? "id";
    const pkType = getPkType(tableSchema);
    let { param, filterText } = getFilterObj(filterString);

    const { params: cfParams, filterTexts: cfFilterTexts } = buildColumnFilters(columnFilters, table);
    if (cfParams.length > 0) {
        param += cfParams.map((p) => `, ${p}`).join("");
    }

    const allFilterTexts: string[] = [];
    if (filterText) allFilterTexts.push(filterText);
    allFilterTexts.push(...cfFilterTexts);

    if (allFilterTexts.length > 1) {
        filterText = `{and: [${allFilterTexts.join(", ")}]}`;
    } else if (allFilterTexts.length === 1) {
        filterText = allFilterTexts[0];
    } else {
        filterText = "";
    }

    const dataColumns = table.columns
        .filter((x: Column) => (x as ColumnWithJoin)?.joinTable === undefined)
        .map((x: Column): ColumnWithJoin => {
            const joinTable = tableSchema.singleJoins.find((j: Join) => j.sourceColumnNames?.[0] === x.name);
            if (!joinTable) return x;
            const joinSchema = schema.findTable(joinTable.destinationTable);
            const labelColumn = joinSchema?.labelColumn ?? "id";
            return {...x, joinTable, joinLabelColumn: labelColumn};
        })
        .map((x: ColumnWithJoin) => {
            if (x?.joinTable) {
                return x.name + ` ${x.joinTable.destinationTable} { id: ${x.joinTable.destinationColumnNames?.[0]} label: ${x.joinLabelColumn} }`;
            }
            return x.name;
        })
        .join(' ');

    if (id && !tableFilter && !filterColumn) {
        param = `, $id: ${pkType}`;
        filterText = `{ ${ primaryKey }: { _eq: $id}}`;
    } else if (id && (filterColumn || tableFilter)) {
        // filterColumn: explicit FK column on the child table (from join metadata)
        // tableFilter: parent table name (from URL routes like /submissions/from/assignments/6)
        const fkColumn = filterColumn
            ?? tableSchema.singleJoins.find((j: Join) => j.destinationTable === tableFilter)?.sourceColumnNames?.[0];
        const fkCol = table.columns.find((c: Column) => c.name === fkColumn);
        const idType = fkCol ? getGraphQlType(fkCol.paramType) : "Int";
        param = `, $id: ${idType}` + param;
        if (fkColumn) {
            // Direct FK filter — no JOIN needed
            if (filterText)
                filterText = `{and: [${filterText}, { ${fkColumn}: { _eq: $id}} ]}`;
            else
                filterText = `{ ${fkColumn}: { _eq: $id}}`;
        } else {
            // Fallback: nested filter through join (requires JOIN support)
            const parentTable = schema.findTable(tableFilter!);
            const parentPk = parentTable?.primaryKeys?.[0] ?? "id";
            if (filterText)
                filterText = `{and: [${filterText}, { ${tableFilter}: { ${ parentPk }: { _eq: $id}}} ]}`;
            else
                filterText = `{ ${tableFilter}: { ${ parentPk }: { _eq: $id}}}`;
        }
    }

    if (filterText) filterText = `filter: ${filterText}`;
    return `query Get${table.name}($sort: [${table.graphQlName}SortEnum!], $limit: Int, $offset: Int ${param}) { ${table.name}(sort: $sort limit: $limit offset: $offset ${filterText}) { total offset limit data {${dataColumns}}}}`;
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

interface UseDataTableResult {
    columns: ColumnDef<RowData, unknown>[];
    sorting: SortingState;
    columnFilters: ColumnFiltersState;
    rowIdField: string;
    pageIndex: number;
    pageSize: number;
    pageCount: number;
    rows: RowData[];
    loading: boolean;
    error: Error | null;
    onSortingChange: (sorting: SortingState) => void;
    onColumnFiltersChange: (filters: ColumnFiltersState) => void;
    onPageIndexChange: (pageIndex: number) => void;
    onPageSizeChange: (pageSize: number) => void;
}

export function useDataTable(table: Table | null, id?: string, filterTable?: string, filterColumn?: string, onDeleteRow?: (pk: string) => void, onExpandContent?: (rowIndex: number, columnName: string) => void, onOpenColumn?: (panel: ColumnPanel) => void): UseDataTableResult {
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

    const pkType = table ? getPkType(table) : "Int";
    const offset = pageIndex * pageSize;

    const queryVariables = useMemo(() => ({
        sort: appliedSort,
        limit: pageSize,
        offset,
        ...(!id ? {} : { id: numericTypes.includes(pkType) ? +id : id }),
        ...filterVariables,
        ...cfVariables,
    }), [appliedSort, pageSize, offset, id, pkType, filterVariables, cfVariables]);

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

    const rowIdField = table?.primaryKeys?.[0] ?? 'id';

    return {
        columns,
        sorting,
        columnFilters,
        rowIdField,
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
