import { useQuery } from "@tanstack/react-query";
import { Link, useSearchParams } from "./usePath";
import { useCallback, useMemo, useState } from "react";
import { useSchema } from "./useSchema";
import { Table, Column, Join, Schema } from "../types/schema";
import { TableColumn, SortOrder } from "react-data-table-component";
import { useFetcher, GraphQLFetcher } from "../common/fetcher";

const numericTypes = ["Int", "Int!", "Float", "Float!"];

interface FilterResult {
    variables: Record<string, unknown>;
    param: string;
    filterText: string;
}

interface SortState {
    columnName: string;
    order: 'asc' | 'desc';
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

const getTableColumns = (table: Table, schema: Schema): TableColumn<RowData>[] => {
    if (!table || !schema) return [];
    const columns = table.columns
        .map((c: Column) => {
            const result = {
                name: c.label,
                reorder: true,
                sortable: true,
                sortField: c.name,
            };
            const singleJoin = table.singleJoins.find((j: Join) => j.sourceColumnNames?.[0] === c.name);
            if (singleJoin) {
                const columnName = singleJoin.destinationTable;
                const joinSchema = schema.findTable(singleJoin.destinationTable);
                return {
                    cell: (row: RowData) => (!!row && <Link to={"/" + joinSchema?.name + "/" + getJoinedRowPkValue(row?.[columnName] as RowData)}>{(row?.[columnName] as RowData)?.label as string}</Link>),
                    ...result
                }
            }
            if ((c as ColumnWithJoin)?.joinTable) {
                return {
                    cell: (row: RowData) => (!!row && <Link to={"/" + c.name + "/" + getJoinedRowPkValue(row?.[c.name] as RowData)}>{c.name}</Link>),
                    ...result
                }
            }
            if (c?.paramType === "DateTime") {
                return {
                    selector: (row: RowData) => (!!c?.name && toLocaleDate(row?.[c?.name] as string)) || "",
                    ...result
                }
            }
            return {
                selector: (row: RowData) => (!!c?.name ? String(row?.[c?.name] ?? "") : ""),
                ...result
            };
        });

    const multiJoins = table.multiJoins
        .map((j: Join) => {
            const joinTable = schema.findTable(j.destinationTable);
            return {
                name: joinTable?.name,
                cell: (row: RowData) => (!!row && <Link to={"/" + joinTable?.name + "/from/" + table.name + "/" + getRowPkValue(row, table)}>{joinTable?.name}</Link>),
                reorder: false,
                sortable: false,
                sortField: j.sourceColumnNames?.[0],
            }
        });
    if (table.isEditable === false)
        return [...columns, ...multiJoins];

    return [{
        name: "edit",
        cell: (row: RowData) => (
            <Link to={`/${table.graphQlName}/edit/${getRowPkValue(row, table)}`}>edit</Link>
        )
    }, ...columns, ...multiJoins];
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

interface DataTableColumn {
    sortField?: string;
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
    tableColumns: TableColumn<RowData>[];
    offset: number;
    limit: number;
    handleSort: (column: DataTableColumn, sortDirection: SortOrder) => void;
    handlePage: (page: number) => void;
    handlePageSize: (size: number) => void;
    loading: boolean;
    error: Error | null;
    data: QueryData | undefined;
}

export function useDataTable(table: Table | null, id?: string, filterTable?: string): UseDataTableResult {
    const { search } = useSearchParams();
    const filterString = search.get('filter') ?? '';
    const { variables: filterVariables } = getFilterObj(filterString);
    const schema = useSchema();
    const fetcher = useFetcher();

    const [sort, setSort] = useState<SortState | null>(null);
    const [offset, setOffset] = useState(0);
    const [limit, setLimit] = useState(10);

    const appliedSort = sort
        ? [`${sort.columnName}_${sort.order}`]
        : table ? [`${table.columns.at(0)?.name ?? 'id'}_asc`] : [];

    const query = useMemo(
        () => buildQuery(table!, schema, filterString, id, filterTable),
        [table, schema, filterString, id, filterTable]
    );

    const pkType = table ? getPkType(table) : "Int";

    const queryVariables = useMemo(() => ({
        sort: appliedSort,
        limit,
        offset,
        ...(!id ? {} : { id: numericTypes.includes(pkType) ? +id : id }),
        ...filterVariables,
    }), [appliedSort, limit, offset, id, pkType, filterVariables]);

    const { isLoading, error, data } = useQuery({
        queryKey: ['tableData', table?.name, queryVariables],
        queryFn: () => fetcher.query<QueryData>(query!, queryVariables),
        enabled: !!query && !!table && appliedSort.length > 0,
    });

    const tableColumns = useMemo(
        () => table ? getTableColumns(table, schema) : [],
        [table, schema]
    );

    const handleSort = useCallback((column: DataTableColumn, sortDirection: SortOrder) => {
        setSort({ columnName: column.sortField ?? 'id', order: sortDirection as 'asc' | 'desc' });
    }, []);

    const handlePage = useCallback((page: number) => {
        setOffset((page - 1) * limit);
    }, [limit]);

    const handlePageSize = useCallback((size: number) => {
        setLimit(size);
    }, []);

    return {
        tableColumns,
        offset,
        limit,
        handleSort,
        handlePage,
        handlePageSize,
        loading: isLoading,
        error: error as Error | null,
        data,
    };
}
