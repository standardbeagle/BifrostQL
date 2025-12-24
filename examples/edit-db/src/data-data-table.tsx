import { useEffect, useState } from 'react'
import DataTable from 'react-data-table-component';
import { useDataTable } from './hooks/useDataTable';
import { Table } from './types/schema';


interface DataDataTableParams {
    table: Table,
    id?: string,
    tableFilter?: string
}

export function DataDataTable({ table, id, tableFilter }: DataDataTableParams): JSX.Element {
    const {
        limit,
        tableColumns,
        handleSort,
        handlePage,
        handlePageSize,
        loading,
        error,
        data } = useDataTable(table, id, tableFilter);

    const [resetPage, setResetpage] = useState(false);
    useEffect(() => {
        if (data && data[table.name]?.offset === 0) {
            setResetpage(prev => !prev);
        }
    }, [data, table.name]);
    if (error) return <div>Error: {error.message}</div>;

    return <DataTable
        columns={tableColumns}
        data={(data?.[table.name]?.data) ?? []}
        progressPending={loading}
        sortServer
        onSort={handleSort}
        pagination
        paginationServer
        paginationTotalRows={(data?.[table.name]?.total) ?? 0}
        paginationPerPage={limit}
        paginationDefaultPage={1}
        paginationResetDefaultPage={resetPage}
        onChangePage={handlePage}
        onChangeRowsPerPage={handlePageSize}
    />;
}