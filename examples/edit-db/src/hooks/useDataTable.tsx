import { DocumentNode, OperationVariables, QueryResult, gql, useQuery } from "@apollo/client";
import { Link, useSearchParams } from "./usePath";
import { useCallback, useEffect, useState } from "react";
import { useSchema } from "./useSchema";
import { Table, Column, Join, Schema } from "../types/schema";
import { TableColumn, SortOrder } from "react-data-table-component";

interface FilterResult {
    variables: Record<string, unknown>;
    param: string;
    filterText: string;
}

interface SortState {
    table: Table;
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
    } catch (ex) {
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
                const joinTable = schema.findTable(singleJoin.destinationTable);
                return {
                    cell: (row: RowData) => (!!row && <Link to={"/" + joinTable?.name + "/" + (row?.[columnName] as RowData)?.id}>{(row?.[columnName] as RowData)?.label as string}</Link>),
                    ...result
                }
            }
            if ((c as ColumnWithJoin)?.joinTable) {
                return {
                    cell: (row: RowData) => (!!row && <Link to={"/" + c.name + "/" + (row?.[c.name] as RowData)?.id}>{c.name}</Link>),
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
                cell: (row: RowData) => (!!row && <Link to={"/" + joinTable?.name + "/from/" + table.name + "/" + row?.id}>{joinTable?.name}</Link>),
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
            <Link to={`/${table.graphQlName}/edit/${row?.id}`}>edit</Link>
        )
    }, ...columns, ...multiJoins];
}

const emptyQuery = gql`query {__schema { __typename }}`;
//useQuery fails when query is null, so I have to use a dummy query, even though it will never be called
const getFilteredQuery = (table: Table | null, search: URLSearchParams, id?: string, tableFilter?: string, schema?: Schema): [runQuery: boolean, query: DocumentNode] => {
    if (!table || !schema?.data) return [false, emptyQuery];
    const tableSchema = schema.findTable(table.graphQlName);
    if (!tableSchema) return [false, emptyQuery];
    const primaryKey = tableSchema?.primaryKeys?.[0] ?? "id";
    let { param, filterText } = getFilterObj(search.get('filter') ?? '');
    //The columns output in the grid
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
    //Don't merge filter because this only has one record by the primary key
    if (id && !tableFilter) {
        param = ", $id: Int";
        filterText = `{ ${ primaryKey }: { _eq: $id}}`;
    }
    if (id && tableFilter) {
        param = ", $id: Int" + param;
        if (filterText)
            filterText = `{and: [${filterText}, { ${tableFilter}: { ${ primaryKey }: { _eq: $id}}} ]}`;
        else
            filterText = `{ ${tableFilter}: { ${ primaryKey }: { _eq: $id}}}`;
    }

    if (filterText) filterText = `filter: ${filterText}`;
    return [true, gql`query Get${table.name}($sort: [${table.graphQlName}SortEnum!], $limit: Int, $offset: Int ${param}) { ${table.name}(sort: $sort limit: $limit offset: $offset ${filterText}) { total offset limit data {${dataColumns}}}}`];
}

const getAppliedSort = (table: Table | null, sort: SortState[]): string[] => {
    if (!table) return [];
    if (!sort || sort.length === 0) return [`${table.columns.at(0)?.name}_asc`];
    return sort
        .filter((s: SortState) => s.table.name === table.name)
        .map((s: SortState) => `${s.columnName}_${s.order}`);
};

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

interface UseDataTableResult extends Partial<QueryResult<QueryData, OperationVariables>> {
    tableColumns: TableColumn<RowData>[];
    offset: number;
    limit: number;
    handleSort: (column: DataTableColumn, sortDirection: SortOrder) => void;
    handlePage: (page: number) => void;
    handlePageSize: (size: number) => void;
    handleUpdate: <T>(value: T) => Promise<T>;
}

export function useDataTable(table: Table | null, id?: string, filterTable?: string): UseDataTableResult {
    const idObj = !id ? {} : { id: +id };
    const filterTableObj = !filterTable ? {} : { filterTable };
    const routeObj = { ...idObj, ...filterTableObj };
    const { search } = useSearchParams();
    const { variables } = getFilterObj(search.get('filter') ?? '');
    const schema = useSchema();

    const [sort, setSort] = useState<SortState[]>([]);
    const [offset, setOffset] = useState(0);
    const [limit, setLimit] = useState(10);
    const [result, setResult] = useState<QueryResult<QueryData, OperationVariables>>();
    const [runQuery, query] = getFilteredQuery(table, search, id, filterTable, schema);
    const tableColumns = table ? getTableColumns(table, schema) : [];
    const appliedSort = getAppliedSort(table, sort);
    const skip = !runQuery || (appliedSort?.length ?? 0) === 0;

    const queryResult = useQuery<QueryData>(query, { skip: skip, variables: { sort: appliedSort, limit: limit, offset: offset, ...routeObj, ...variables } });
    const handleUpdate = useCallback(<T,>(value: T): Promise<T> => {
        return Promise.resolve(value);
    }, []);

    useEffect(() => {
        if (!table) return;
        setSort([{
            table,
            columnName: table.columns.at(0)?.name ?? 'id',
            order: 'asc'
        }]);
        setOffset(0);
    }, [table, id, filterTable]);

    useEffect(() => {
        if (!query) return;
        setResult(queryResult);
    }, [queryResult, query]);

    const handleSort = useCallback((column: DataTableColumn, sortDirection: SortOrder) => {
        const newSort = [`${column.sortField}_${sortDirection}`];
        setSort([{ table: table!, columnName: column.sortField ?? 'id', order: sortDirection as 'asc' | 'desc' }]);
        queryResult.refetch({ sort: newSort, limit: limit, offset: offset, ...routeObj })
    }, [queryResult, offset, limit, routeObj, table]);

    const handlePage = useCallback((page: number) => {
        const newOffset = +((page - 1) * limit);
        setOffset(newOffset);
        const appliedSort = getAppliedSort(table, sort);
        queryResult.refetch({ sort: appliedSort, limit: limit, offset: newOffset, ...routeObj });
    }, [queryResult, limit, sort, routeObj, table]);

    const handlePageSize = useCallback((size: number) => {
        setLimit(size);
        const appliedSort = getAppliedSort(table, sort);
        queryResult.refetch({ sort: appliedSort, limit: size, offset: offset, ...routeObj });
    }, [queryResult, offset, sort, routeObj, table]);

    return { tableColumns, offset, limit, handleSort, handlePage, handlePageSize, handleUpdate, ...result };
}

