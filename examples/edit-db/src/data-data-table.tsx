import React from 'react'
import { useQuery, gql } from '@apollo/client';
import DataTable from 'react-data-table-component';
import { useSearchParams } from './hooks/usePath';

const getFilterObj = (filterString: string ) : any => {
    try {
        if (!filterString) return { variables: {}, param: "", filterText: ""};
        const [column, action, value, type] = JSON.parse(filterString);
        return { variables: { filter: value }, param: `, $filter: ${type}`, filterText: ` filter: {${column}: {${action}: $filter} }`}
    } catch (ex) {
        return { variables: {}, param: "", filterText: ""};
    }
}

export function DataDataTable({table}:{table:any }): JSX.Element {
    const singleColumns = table.columns
    .filter((c : any) => (c?.type?.kind !== "LIST" && c?.type?.kind !== "OBJECT"));
    console.log('sc', singleColumns);
    const tableColumns = singleColumns
    .map((c: any) => console.log(c) || ({
        name: c.name,
        selector: (row: { [x: string]: any; }) => row[c.name],
        reorder: true,
        sortable: true,
        sortField: c.name,
    }));
    const sort:any[] = [ tableColumns[0].name + " asc"];
    const offset = 0;
    const limit = 10; 
    const {search} = useSearchParams();
    const {variables, param, filterText } = getFilterObj(search.get('filter'));
    const query = gql`query Get${table.name}($sort: [String], $limit: Int, $offset: Int ${param}) { ${table.name}(sort: $sort limit: $limit offset: $offset ${filterText}) { total offset limit data {${ singleColumns.map((x: { name: any; }) => x.name).join(' ')}}}}`;
    const {loading, error, data, refetch} = useQuery(query, { variables: { sort: sort, limit: limit, offset: offset, ...variables }});

    if (error) return <div>Error: {error.message}</div>;

    const handleSort = (column: any, sortDirection: any, test: any) => {
            const search = {offset: offset, sort: [`${column.sortField} ${sortDirection}`]};
            refetch({ sort: search.sort, limit: limit, offset: offset })
        }
    const handlePage = (page: number) => {
            const search = {offset: +(page*limit), sort: sort };
            refetch({ sort: sort, limit: limit, offset: search.offset });
        }
    const handlePageSize = (size: number) => {
        refetch({ sort: sort, limit: size, offset: offset });
    }

    return <DataTable 
        columns={tableColumns} 
        data={data ? data[table.name].data : []}
        progressPending={loading}
        sortServer
        onSort={handleSort}
        pagination
        paginationServer
        paginationTotalRows={data ? data[table.name].total : 0}
        paginationPerPage={limit}
        onChangePage={handlePage}
        onChangeRowsPerPage={handlePageSize}
        />;
}