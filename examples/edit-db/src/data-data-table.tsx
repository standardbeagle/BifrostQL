import React from 'react'
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

    if (error) return <div>Error: {error.message}</div>;

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