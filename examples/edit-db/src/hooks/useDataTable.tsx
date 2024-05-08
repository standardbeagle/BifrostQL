import { DocumentNode, OperationVariables, QueryResult, gql, useQuery } from "@apollo/client";
import { Link, useSearchParams } from "./usePath";
import { useCallback, useEffect, useState } from "react";
import { useSchema } from "./useSchema";

const getFilterObj = (filterString: string): any => {
    try {
        if (!filterString) return { variables: {}, param: "", filterText: "" };
        const [column, action, value, type] = JSON.parse(filterString);
        return { variables: { filter: value }, param: `, $filter: ${type}`, filterText: ` filter: {${column}: {${action}: $filter} }` }
    } catch (ex) {
        return { variables: {}, param: "", filterText: "" };
    }
}

const toLocaleDate = (d: string): String => {
    if (!d) return "";
    var dd = new Date(d);
    if (dd.toString() === "invalid date") return "";
    if (dd < new Date('1973-01-01')) return "";
    return dd.toLocaleString();
};

const getTableColumns = (table: any, schema: any): any[] => {
    if (!table || !schema) return [];
    const columns = table.columns
        .map((c: any) => {
            const result = {
                name: c.label,
                reorder: true,
                sortable: true,
                sortField: c.name,
            };
            const singleJoin = table.singleJoins.find((j: any) => j.sourceColumnNames?.[0] === c.name);
            console.log({singleJoin});
            if (singleJoin) {
                const columnName = singleJoin.destinationTable;
                const joinTable = schema.findTable(singleJoin.destinationTable);
                return {
                    selector: (row: { [x: string]: any; }) => (!!row && <Link to={"/" + joinTable.name + "/" + row?.[columnName]?.id}>{row?.[columnName]?.label}</Link>),
                    ...result
                }
            }
            if (c?.type?.kind === "OBJECT") {
                return {
                    selector: (row: { [x: string]: any; }) => (!!row && <Link to={"/" + c.name + "/" + row?.[c.name]?.id}>{c.name}</Link>),
                    ...result
                }
            }
            if (c?.type?.kind === "LIST") {
                return {
                    selector: (row: { [x: string]: any; }) => (<Link to={"/" + c.name + "/from/" + table.name + "/" + row["id"]}>{c.name}</Link>),
                    ...result
                }
            }
            if (c?.paramType === "DateTime") {
                return {
                    selector: (row: { [x: string]: any; }) => ((!!c?.name && toLocaleDate(row?.[c?.name])) ?? ""),
                    ...result
                }
            }
            return {
                selector: (row: { [x: string]: any; }) => (!!c?.name && row?.[c?.name]) ?? "",
                ...result
            };
        });
    if (table.isEditable === false)
        return columns;

    return [{
        name: "edit", selector: (row: { [x: string]: any; }) => (
            <Link to={`edit/${row?.id}`}>edit</Link>
        )
    }, ...columns];
}

const emptyQuery = gql`query {__schema { __typename }}`;
//useQuery fails when query is null, so I have to use a dummy query, even though it will never be called
const getFilteredQuery = (table: any, search: any, id?: string, tableFilter?: string, schema?:any): [runQuery: boolean, query: DocumentNode] => {
    if (!table || !schema?.data) return [false, emptyQuery];
    const tableSchema = schema.findTable(table.graphQlName);
    if (!tableSchema) return [false, emptyQuery];
    //console.log({schema});
    const primaryKey = tableSchema?.primaryKeys?.[0] ?? "id";
    let { param, filterText } = getFilterObj(search.get('filter'));
    //The columns output in the grid
    const dataColumns = table.columns
        .filter((x: { type: any }) => x?.type?.kind !== "LIST")
        .map((x: { name: string, type: any }) => {
            const joinTable = tableSchema.singleJoins.find((j: any) => j.sourceColumnNames?.[0] === x.name);
            if (!joinTable) return x;

            const joinSchema = schema.findTable(joinTable.destinationTable);
            const labelColumn = joinSchema?.labelColumn ?? "id";
            //console.log({joinTable, joinSchema, test: labelColumn});
            return {...x, joinTable, joinLabelColumn: labelColumn}; 
        })
        .map((x: { name: string, type: any, joinTable: any, joinLabelColumn?: string }) => {
            if (x?.type?.kind === "OBJECT") {
                return x.name + "{ id }";
            }
            if (x?.joinTable) {

                return x.name + ` ${x.joinTable.destinationTable} { id: ${x.joinTable.destinationColumnNames?.[0]} label: ${x.joinLabelColumn} }`;
            }
            return x.name;
        })
        .join(' ');
    if (id && !tableFilter) {
        param = ", $id: Int";
        filterText = `filter: { ${ primaryKey }: { _eq: $id}}`;
    }
    if (id && tableFilter) {
        param = ", $id: Int";
        filterText = `filter: { ${tableFilter}: { ${ primaryKey }: { _eq: $id}}}`;
    }

    return [true, gql`query Get${table.name}($sort: [${table.graphQlName}SortEnum!], $limit: Int, $offset: Int ${param}) { ${table.name}(sort: $sort limit: $limit offset: $offset ${filterText}) { total offset limit data {${dataColumns}}}}`];
}

const getAppliedSort = (table: any, sort: any[]): string[] => {
    if (!table) return [];
    if (!sort) return [`${table.columns.at(0)?.name}_asc`];
    return sort
        .filter((s: any) => s.table.name === table.name)
        .map((s: any) => `${s.columnName}_${s.order}`);
};


export function useDataTable(table: any, id?: string, filterTable?: string) {
    const idObj = !id ? {} : { id: +id };
    const filterTableObj = !filterTable ? {} : { filterTable };
    const routeObj = { ...idObj, ...filterTableObj };
    const { search } = useSearchParams();
    let { variables } = getFilterObj(search.get('filter'));
    const schema = useSchema();

    const [sort, setSort] = useState<any[]>([]);
    const [offset, setOffset] = useState(0);
    const [limit, setLimit] = useState(10);
    const [result, setResult] = useState<QueryResult<any, OperationVariables>>();
    const [runQuery, query] = getFilteredQuery(table, search, id, filterTable, schema);
    const tableColumns = getTableColumns(table, schema);
    const appliedSort = getAppliedSort(table, sort);

    const queryResult = useQuery(query, { skip: !runQuery, variables: { sort: appliedSort, limit: limit, offset: offset, ...routeObj, ...variables } });
    const handleUpdate = useCallback((value: any): Promise<any> => {
        return Promise.resolve(value);
    }, []);

    useEffect(() => {
        if (!table) return;
        setSort([{
            table,
            columnName: table.columns.at(0)?.name,
            order: 'asc'
        }]);
        setOffset(0);
    }, [table, id, filterTable]);

    useEffect(() => {
        if (!query) return;
        setResult(queryResult);
    }, [queryResult, query]);

    const handleSort = useCallback((column: any, sortDirection: any) => {
        const newSort = [`${column.sortField}_${sortDirection}`];
        setSort([{ table, columnName: column.sortField, order: sortDirection }]);
        const search = { offset: offset, sort: newSort };
        queryResult.refetch({ sort: search.sort, limit: limit, offset: offset, ...routeObj })
    }, [queryResult, offset, limit, routeObj]);

    const handlePage = useCallback((page: number) => {
        var newOffset = +((page - 1) * limit);
        setOffset(newOffset);
        const search = { offset: newOffset, sort: sort };
        const appliedSort = getAppliedSort(table, sort);
        queryResult.refetch({ sort: appliedSort, limit: limit, offset: search.offset, ...routeObj });
    }, [queryResult, limit, sort, routeObj]);

    const handlePageSize = useCallback((size: number) => {
        setLimit(size);
        const appliedSort = getAppliedSort(table, sort);
        queryResult.refetch({ sort: appliedSort, limit: size, offset: offset, ...routeObj });
    }, [queryResult, offset, sort, routeObj]);

    return { tableColumns, offset, limit, handleSort, handlePage, handlePageSize, handleUpdate, ...result };
}

