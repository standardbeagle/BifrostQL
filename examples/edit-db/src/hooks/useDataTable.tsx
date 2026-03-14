import { useQuery } from "@tanstack/react-query";
import { Link, useSearchParams } from "./usePath";
import { useCallback, useMemo, useState } from "react";
import { useSchema } from "./useSchema";
import { Table, Column, Join, Schema } from "../types/schema";
import { ColumnDef, SortingState } from "@tanstack/react-table";
import { useFetcher } from "../common/fetcher";
import { DataTableColumnHeader } from "../components/data-table-column-header";

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

const getTableColumns = (table: Table, schema: Schema): ColumnDef<RowData, unknown>[] => {
    if (!table || !schema) return [];

    const editColumn: ColumnDef<RowData, unknown>[] = table.isEditable !== false
        ? [{
            id: 'edit',
            header: 'Edit',
            enableSorting: false,
            enableHiding: false,
            cell: ({ row }) => (
                <Link to={`/${table.graphQlName}/edit/${getRowPkValue(row.original, table)}`} className="text-primary hover:text-primary/80 hover:underline text-sm font-medium">edit</Link>
            ),
        }]
        : [];

    const dataColumns: ColumnDef<RowData, unknown>[] = table.columns
        .map((c: Column): ColumnDef<RowData, unknown> => {
            const singleJoin = table.singleJoins.find((j: Join) => j.sourceColumnNames?.[0] === c.name);

            if (singleJoin) {
                const columnName = singleJoin.destinationTable;
                const joinSchema = schema.findTable(singleJoin.destinationTable);
                return {
                    id: c.name,
                    accessorKey: c.name,
                    header: ({ column }) => <DataTableColumnHeader column={column} title={c.label} />,
                    enableSorting: true,
                    meta: { sortField: c.name },
                    cell: ({ row }) => {
                        const joined = row.original[columnName] as RowData | undefined;
                        if (!joined) return null;
                        return (
                            <Link to={"/" + joinSchema?.name + "/" + getJoinedRowPkValue(joined)} className="text-primary hover:text-primary/80 hover:underline">
                                {joined?.label as string}
                            </Link>
                        );
                    },
                };
            }

            if ((c as ColumnWithJoin)?.joinTable) {
                return {
                    id: c.name,
                    accessorKey: c.name,
                    header: ({ column }) => <DataTableColumnHeader column={column} title={c.label} />,
                    enableSorting: true,
                    meta: { sortField: c.name },
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
                    header: ({ column }) => <DataTableColumnHeader column={column} title={c.label} />,
                    enableSorting: true,
                    meta: { sortField: c.name },
                };
            }

            return {
                id: c.name,
                accessorFn: (row) => (c.name ? String(row?.[c.name] ?? "") : ""),
                header: ({ column }) => <DataTableColumnHeader column={column} title={c.label} />,
                enableSorting: true,
                meta: { sortField: c.name },
            };
        });

    const multiJoinColumns: ColumnDef<RowData, unknown>[] = table.multiJoins
        .map((j: Join): ColumnDef<RowData, unknown> => {
            const joinTable = schema.findTable(j.destinationTable);
            return {
                id: `join_${j.destinationTable}`,
                header: joinTable?.name ?? j.destinationTable,
                enableSorting: false,
                enableHiding: true,
                cell: ({ row }) => (
                    <Link to={"/" + joinTable?.name + "/from/" + table.name + "/" + getRowPkValue(row.original, table)} className="text-primary hover:text-primary/80 hover:underline">
                        {joinTable?.name}
                    </Link>
                ),
            };
        });

    return [...editColumn, ...dataColumns, ...multiJoinColumns];
}

const getPkType = (table: Table): string => {
    const pkName = table.primaryKeys?.[0];
    if (!pkName) return "Int";
    const pkColumn = table.columns.find((c: Column) => c.name === pkName);
    return pkColumn?.paramType?.replace("!", "") ?? "Int";
};

const buildQuery = (table: Table, schema: Schema, filterString: string, id?: string, tableFilter?: string): string | null => {
    if (!table || !schema?.data) return null;
    const tableSchema = schema.findTable(table.graphQlName);
    if (!tableSchema) return null;
    const primaryKey = tableSchema?.primaryKeys?.[0] ?? "id";
    const pkType = getPkType(tableSchema);
    let { param, filterText } = getFilterObj(filterString);

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

    if (id && !tableFilter) {
        param = `, $id: ${pkType}`;
        filterText = `{ ${ primaryKey }: { _eq: $id}}`;
    }
    if (id && tableFilter) {
        param = `, $id: ${pkType}` + param;
        if (filterText)
            filterText = `{and: [${filterText}, { ${tableFilter}: { ${ primaryKey }: { _eq: $id}}} ]}`;
        else
            filterText = `{ ${tableFilter}: { ${ primaryKey }: { _eq: $id}}}`;
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
    pageIndex: number;
    pageSize: number;
    pageCount: number;
    rows: RowData[];
    loading: boolean;
    error: Error | null;
    onSortingChange: (sorting: SortingState) => void;
    onPageIndexChange: (pageIndex: number) => void;
    onPageSizeChange: (pageSize: number) => void;
}

export function useDataTable(table: Table | null, id?: string, filterTable?: string): UseDataTableResult {
    const { search } = useSearchParams();
    const filterString = search.get('filter') ?? '';
    const { variables: filterVariables } = getFilterObj(filterString);
    const schema = useSchema();
    const fetcher = useFetcher();

    const [sorting, setSorting] = useState<SortingState>([]);
    const [pageIndex, setPageIndex] = useState(0);
    const [pageSize, setPageSize] = useState(10);

    const appliedSort = useMemo(() => {
        if (sorting.length > 0) {
            const col = sorting[0];
            return [`${col.id}_${col.desc ? 'desc' : 'asc'}`];
        }
        return table ? [`${table.columns.at(0)?.name ?? 'id'}_asc`] : [];
    }, [sorting, table]);

    const query = useMemo(
        () => buildQuery(table!, schema, filterString, id, filterTable),
        [table, schema, filterString, id, filterTable]
    );

    const pkType = table ? getPkType(table) : "Int";
    const offset = pageIndex * pageSize;

    const queryVariables = useMemo(() => ({
        sort: appliedSort,
        limit: pageSize,
        offset,
        ...(!id ? {} : { id: numericTypes.includes(pkType) ? +id : id }),
        ...filterVariables,
    }), [appliedSort, pageSize, offset, id, pkType, filterVariables]);

    const { isLoading, error, data } = useQuery({
        queryKey: ['tableData', table?.name, queryVariables],
        queryFn: () => fetcher.query<QueryData>(query!, queryVariables),
        enabled: !!query && !!table && appliedSort.length > 0,
    });

    const columns = useMemo(
        () => table ? getTableColumns(table, schema) : [],
        [table, schema]
    );

    const tableData = data?.[table?.name ?? ''];
    const rows = tableData?.data ?? [];
    const totalRows = tableData?.total ?? 0;
    const pageCount = Math.max(1, Math.ceil(totalRows / pageSize));

    const onSortingChange = useCallback((newSorting: SortingState) => {
        setSorting(newSorting);
        setPageIndex(0);
    }, []);

    const onPageIndexChange = useCallback((newPageIndex: number) => {
        setPageIndex(newPageIndex);
    }, []);

    const onPageSizeChange = useCallback((newPageSize: number) => {
        setPageSize(newPageSize);
        setPageIndex(0);
    }, []);

    return {
        columns,
        sorting,
        pageIndex,
        pageSize,
        pageCount,
        rows,
        loading: isLoading,
        error: error as Error | null,
        onSortingChange,
        onPageIndexChange,
        onPageSizeChange,
    };
}
