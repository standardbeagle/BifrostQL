import { gql, useQuery } from "@apollo/client";
import { Link, useSearchParams } from "./usePath";
import { useEffect, useState } from "react";

const getFilterObj = (filterString: string): any => {
    try {
        if (!filterString) return { variables: {}, param: "", filterText: "" };
        const [column, action, value, type] = JSON.parse(filterString);
        return { variables: { filter: value }, param: `, $filter: ${type}`, filterText: ` filter: {${column}: {${action}: $filter} }` }
    } catch (ex) {
        return { variables: {}, param: "", filterText: "" };
    }
}

const toLocaleDate = (d:string):String => { 
    if (!d) return "";
    var dd = new Date(d);
    if (dd.toString() === "invalid date") return "";
    if (dd < new Date('1973-01-01')) return "";
    return dd.toLocaleString();
};

const getTableColumns = (table:any): any[] => {
    const columns = table.columns
    .map((c: any) => {
        const result = {
            name: c.label,
            reorder: true,
            sortable: true,
            sortField: c.name,
        };
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

    return [{ name: "edit", selector: (row: { [x: string]: any; }) => (
        <Link to={`edit/${row?.id}`}>edit</Link>
    )}, ...columns];
}

const getFilteredQuery = (table:any, search: any, id?:string, filterTable?: string) => {
    let { param, filterText } = getFilterObj(search.get('filter'));
    const searchColumns = table.columns
        .filter((x: { type: any }) => x?.type?.kind !== "LIST")
        .map((x: { name: string, type: any }) => {
            if (x?.type?.kind === "OBJECT") {
                return x.name + "{ id }";
            }
            return x.name;
        })
        .join(' ');
    if (id && !filterTable) {
        param = ", $id: Int";
        filterText = "filter: { id: { _eq: $id}}";
    }
    if (id && filterTable) {
        param = ", $id: Int";
        filterText = `filter: { ${filterTable}: { id: { _eq: $id}}}`;
    }

    return gql`query Get${table.name}($sort: [String], $limit: Int, $offset: Int ${param}) { ${table.name}(sort: $sort limit: $limit offset: $offset ${filterText}) { total offset limit data {${searchColumns}}}}`;
}


export function useDataTable(table: any, id?: string, filterTable?: string) {
    const idObj = !id ? {} : { id: +id };
    const filterTableObj = !filterTable ? {} : { filterTable };
    const routeObj = { ...idObj, ...filterTableObj };
    const tableColumns = getTableColumns(table);
    const { search } = useSearchParams();
    let { variables } = getFilterObj(search.get('filter'));

    const [sort, setSort] = useState<string[]>([`${table.columns.at(0)?.name}_asc`]);
    const [offset, setOffset] = useState(0);
    const [limit, setLimit] = useState(10);
    const query = getFilteredQuery(table, search, id, filterTable);
    const queryResult = useQuery(query, { variables: { sort: sort, limit: limit, offset: offset, ...routeObj, ...variables } });
    const handleUpdate = (value: any) : Promise<any> => {
        return Promise.resolve(value);
    }

    useEffect(() => {  
        setSort([`${table.columns.at(0)?.name}_asc`]);
        setOffset(0);        
     },[table, id, filterTable]);
    
    const handleSort = (column: any, sortDirection: any) => {
        const newSort = [`${column.sortField}_${sortDirection}`];
        setSort(newSort);
        const search = { offset: offset, sort: newSort };
        queryResult.refetch({ sort: search.sort, limit: limit, offset: offset, ...routeObj })
    }
    const handlePage = (page: number) => {
        var newOffset = +((page-1) * limit);
        setOffset(newOffset);
        const search = { offset: newOffset, sort: sort };
        queryResult.refetch({ sort: sort, limit: limit, offset: search.offset, ...routeObj });
    }
    const handlePageSize = (size: number) => {
        setLimit(size);
        queryResult.refetch({ sort: sort, limit: size, offset: offset, ...routeObj });
    }

    return { tableColumns, offset, limit, handleSort, handlePage, handlePageSize, handleUpdate, ...queryResult };
}

