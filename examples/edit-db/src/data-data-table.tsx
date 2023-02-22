import React, { useEffect, useState } from 'react'
import DataTable from 'react-data-table-component';
import { useDataTable } from './hooks/useDataTable';


interface DataDataTableParams {
    table: any,
    id?: string,
    filterTable?: string
}

export function DataDataTable({ table, id, filterTable }: DataDataTableParams): JSX.Element {
    const {
        limit,
        tableColumns,
        handleSort,
        handlePage,
        handlePageSize,
        loading,
        error,
        data } = useDataTable(table, id, filterTable);

    const [resetPage, setResetpage] = useState(false);
    if (error) return <div>Error: {error.message}</div>;
    //This is required to make paging reset when loading a new dataset, it probably doesn't support all the edge cases yet.
    useEffect(() => { data && data[table.name].offset === 0 && setResetpage(!resetPage);}, [data]);

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
        paginationDefaultPage={1}
        paginationResetDefaultPage={resetPage}
        onChangePage={handlePage}
        onChangeRowsPerPage={handlePageSize}
    />;
}