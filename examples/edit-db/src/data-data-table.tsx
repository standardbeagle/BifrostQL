import { useDataTable } from './hooks/useDataTable';
import { DataTable } from './components/data-table';
import { Table } from './types/schema';

interface DataDataTableParams {
    table: Table;
    id?: string;
    tableFilter?: string;
    selectedRowId?: string | null;
    onRowSelect?: (rowId: string | null) => void;
}

export function DataDataTable({ table, id, tableFilter, selectedRowId, onRowSelect }: DataDataTableParams): JSX.Element {
    const {
        columns,
        sorting,
        columnFilters,
        rowIdField,
        pageIndex,
        pageSize,
        pageCount,
        rows,
        loading,
        error,
        onSortingChange,
        onColumnFiltersChange,
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
            columnFilters={columnFilters}
            loading={loading}
            rowIdField={rowIdField}
            selectedRowId={selectedRowId}
            onRowSelect={onRowSelect}
            onSortingChange={onSortingChange}
            onColumnFiltersChange={onColumnFiltersChange}
            onPageIndexChange={onPageIndexChange}
            onPageSizeChange={onPageSizeChange}
        />
    );
}
