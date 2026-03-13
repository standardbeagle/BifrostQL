import { useDataTable } from './hooks/useDataTable';
import { DataTable } from './components/data-table';
import { Table } from './types/schema';

interface DataDataTableParams {
    table: Table,
    id?: string,
    tableFilter?: string
}

export function DataDataTable({ table, id, tableFilter }: DataDataTableParams): JSX.Element {
    const {
        columns,
        sorting,
        pageIndex,
        pageSize,
        pageCount,
        rows,
        loading,
        error,
        onSortingChange,
        onPageIndexChange,
        onPageSizeChange,
    } = useDataTable(table, id, tableFilter);

    if (error) return <div>Error: {error.message}</div>;

    return (
        <DataTable
            columns={columns}
            data={rows}
            pageCount={pageCount}
            pageIndex={pageIndex}
            pageSize={pageSize}
            sorting={sorting}
            loading={loading}
            onSortingChange={onSortingChange}
            onPageIndexChange={onPageIndexChange}
            onPageSizeChange={onPageSizeChange}
        />
    );
}
